using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Rates;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Npgsql;
using Xunit;
using Invoice = BTCPayServer.Client.Models.InvoiceData;

namespace BTCPayServer.Tests;

public sealed class LocalAttoMethod : IPaymentMethodHandler
{
    public static readonly PaymentMethodId Method = PaymentMethodId.Parse("ATTO-LOCAL");
    public static readonly HttpClient Fixture = new() { BaseAddress = new Uri("http://127.0.0.1:19345/") };
    public PaymentMethodId PaymentMethodId => Method;
    public Newtonsoft.Json.JsonSerializer Serializer => Newtonsoft.Json.JsonSerializer.CreateDefault();
    public object ParsePaymentPromptDetails(JToken details) => details;
    public object ParsePaymentMethodConfig(JToken config) => config;
    public object ParsePaymentDetails(JToken details) => details;
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IPaymentMethodHandler, LocalAttoMethod>();
        services.AddSingleton<CurrencyDataProvider>(new InMemoryCurrencyDataProvider(
            [new CurrencyData { Code = "ATTO", Name = "Local test Atto", Divisibility = 9, Crypto = true }]));
    }
    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = "ATTO";
        context.Prompt.Divisibility = 9;
        return Task.CompletedTask;
    }
    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        using var reply = await Fixture.PostAsync("receivers", null);
        reply.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync());
        var address = doc.RootElement.GetProperty("address").GetString();
        context.Prompt.Destination = address;
        context.Prompt.Details = new JObject { ["publicKey"] = doc.RootElement.GetProperty("publicKey").GetString() };
        context.TrackedDestinations.Add(address);
    }
}

public sealed record ConfirmedSend(string Id, string Address, string Key, decimal Raw);

public sealed class LocalMerchant(BTCPayServerTester host)
{
    private static readonly HttpClient Network = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static NpgsqlConnection MerchantDb() => new(Environment.GetEnvironmentVariable("ATTO_TEST_MERCHANT_DB")
        ?? throw new InvalidOperationException("Missing disposable merchant database"));
    public static async Task Prepare()
    {
        await using var db = MerchantDb();
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS checkout_seen (
                id text PRIMARY KEY, invoice text NOT NULL, address text NOT NULL, raw numeric(20,0) NOT NULL
            );
            CREATE TABLE IF NOT EXISTS checkout_deliveries (
                invoice text PRIMARY KEY, payment text NOT NULL REFERENCES checkout_seen(id)
            );
            """);
    }
    private static ulong UInt(JsonElement obj, string name)
    {
        var part = obj.GetProperty(name);
        return ulong.Parse(part.ValueKind == JsonValueKind.String ? part.GetString() : part.GetRawText(),
            NumberStyles.None, CultureInfo.InvariantCulture);
    }
    public static async Task<ConfirmedSend> ReadSend(string hash)
    {
        var info = await LocalAttoMethod.Fixture.GetFromJsonAsync<JsonElement>("health");
        Assert.Equal("LOCAL", info.GetProperty("network").GetString());
        var node = new Uri(info.GetProperty("node").GetString());
        Assert.True(node.IsLoopback);
        using var tx = JsonDocument.Parse(await Network.GetStringAsync(new Uri(node, "transactions/" + hash)));
        var block = tx.RootElement.GetProperty("block");
        Assert.Equal("LOCAL", block.GetProperty("network").GetString());
        Assert.Equal("SEND", block.GetProperty("type").GetString());
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(node, "accounts/entries/" + hash + "/stream"));
        req.Headers.Accept.ParseAdd("application/x-ndjson");
        using var result = await Network.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        result.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await result.Content.ReadAsStreamAsync());
        using var line = JsonDocument.Parse(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        var entry = line.RootElement;
        Assert.Equal(hash, entry.GetProperty("hash").GetString());
        Assert.Equal("SEND", entry.GetProperty("blockType").GetString());
        var key = block.GetProperty("receiverPublicKey").GetString();
        Assert.Equal(key, entry.GetProperty("subjectPublicKey").GetString());
        Assert.Equal(block.GetProperty("publicKey").GetString(), entry.GetProperty("publicKey").GetString());
        var raw = checked(UInt(entry, "previousBalance") - UInt(entry, "balance"));
        Assert.Equal(UInt(block,"amount"), raw);
        return new ConfirmedSend("LOCAL:" + hash, block.GetProperty("receiverAddress").GetString(), key, raw);
    }

    public async Task<string> RecordObservation(ConfirmedSend send)
    {
        var invoice = await host.InvoiceRepository.GetInvoiceFromAddress(LocalAttoMethod.Method, send.Address);
        Assert.NotNull(invoice);
        var prompt = invoice.GetPaymentPrompt(LocalAttoMethod.Method);
        Assert.Equal(send.Key, prompt.Details.Value<string>("publicKey"));
        await using var db = MerchantDb();
        await db.ExecuteAsync("""
            INSERT INTO checkout_seen (id,invoice,address,raw) VALUES (@Id,@Invoice,@Address,@Raw)
            ON CONFLICT (id) DO NOTHING
            """, new { send.Id, Invoice = invoice.Id, send.Address, send.Raw });
        var matches = await db.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM checkout_seen WHERE id=@Id AND invoice=@Invoice AND address=@Address AND raw=@Raw
            """, new { send.Id, Invoice = invoice.Id, send.Address, send.Raw });
        Assert.Equal(1, matches);
        return invoice.Id;
    }

    public async Task SavePayment(string paymentId)
    {
        await using var db = MerchantDb();
        var row = await db.QuerySingleAsync<Observation>(
            "SELECT invoice, address, raw FROM checkout_seen WHERE id=@paymentId", new { paymentId });
        var method = new LocalAttoMethod();
        var invoice = await host.InvoiceRepository.GetInvoice(row.Invoice);
        var prompt = invoice.GetPaymentPrompt(method.PaymentMethodId);
        Assert.Equal(row.Address, prompt.Destination);
        var value = row.Raw / 1_000_000_000m;
        var payment = new PaymentData
        {
            Id = paymentId, Amount = value, Currency = "ATTO", Created = DateTimeOffset.UtcNow, Status = PaymentStatus.Settled
        }.Set(invoice, method, new { network = "LOCAL", send = paymentId[6..] });
        await host.GetService<PaymentService>().AddPayment(payment);
        await using var ctx = host.GetService<ApplicationDbContextFactory>().CreateContext();
        var stored = await ctx.Payments.SingleOrDefaultAsync(p => p.Id == paymentId && p.PaymentMethodId == "ATTO-LOCAL");
        Assert.NotNull(stored);
        Assert.Equal(invoice.Id, stored.InvoiceDataId);
        Assert.Equal("ATTO", stored.Currency);
        Assert.Equal(value, stored.Amount);
        Assert.Equal(PaymentStatus.Settled, stored.Status);
        Assert.Equal(row.Address, stored.GetBlob().Destination);
    }
    public void Refresh(string invoiceId) =>
        host.GetService<EventAggregator>().Publish(new InvoiceNeedUpdateEvent(invoiceId));

    public async Task<int> Deliver(string invoiceId)
    {
        var invoice = await host.InvoiceRepository.GetInvoice(invoiceId);
        if (invoice.Status != InvoiceStatus.Settled)
            return 0;
        var paid = invoice.GetPayments(true).Where(p => p.PaymentMethodId == LocalAttoMethod.Method &&
            p.Status == PaymentStatus.Settled && p.Currency == "ATTO").ToList();
        if (paid.Sum(p => p.Value) < invoice.Price || paid.Count != 1)
            return 0;
        await using var db = MerchantDb();
        return await db.ExecuteAsync("""
            INSERT INTO checkout_deliveries(invoice,payment)
            SELECT invoice,id FROM checkout_seen WHERE invoice=@invoiceId AND id=@id AND raw=@raw
            ON CONFLICT (invoice) DO NOTHING
            """,new { invoiceId, id = paid[0].Id, raw = paid[0].Value * 1_000_000_000m });
    }
    private sealed class Observation
    {
        public string Invoice { get; set; }
        public string Address { get; set; }
        public decimal Raw { get; set; }
    }
}

public class AttoCheckoutReplayTests(ITestOutputHelper output) : UnitTestBase(output)
{
    private static async Task<string> Pay(string destination,string raw)
    {
        using var response = await LocalAttoMethod.Fixture.PostAsJsonAsync("payments", new { destination, raw });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hash").GetString();
    }
    private static async Task<Invoice> AwaitInvoice(BTCPayServerClient client,string id,Func<Invoice,bool> condition)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(25);
        Invoice invoice;
        do
        {
            invoice = await client.GetInvoice(id);
            if (condition(invoice)) return invoice;
            await Task.Delay(200);
        } while (DateTimeOffset.UtcNow < end);
        throw new TimeoutException($"Invoice {id}: {invoice.Status}, {invoice.AdditionalStatus}, paid {invoice.PaidAmount}.");
    }

    [Fact(Timeout = 240000)]
    [Trait("AttoMerchant", "AttoMerchant")]
    public async Task CheckoutRestartsUseOnePaymentAndOneDelivery()
    {
        await using var server = CreateServerTester(newDb:true);
        await server.StartAsync();
        var user = server.NewAccount();
        await user.RegisterAsync();
        await user.CreateStoreAsync();
        var host = server.PayTester;
        var store = await host.StoreRepository.FindStore(user.StoreId);
        store.SetPaymentMethodConfig(LocalAttoMethod.Method, new JObject());
        await host.StoreRepository.UpdateStore(store);
        var client = await user.CreateClient();
        await LocalMerchant.Prepare();
        Task<Invoice> NewInvoice() => client.CreateInvoice(user.StoreId, new CreateInvoiceRequest { Amount=1m, Currency="ATTO" });
        async Task<string> Destination(string id) =>
            (await host.InvoiceRepository.GetInvoice(id)).GetPaymentPrompt(LocalAttoMethod.Method).Destination;

        var lateInvoice = await client.CreateInvoice(user.StoreId, new CreateInvoiceRequest
        {
            Amount=1m, Currency="ATTO",
            Checkout=new CreateInvoiceRequest.CheckoutOptions { Expiration=TimeSpan.FromMinutes(1) }
        });

        foreach (var stopAfterBtcpayWrite in new[] { false, true })
        {
            var first = await NewInvoice();
            var second = await NewInvoice();
            var destination = await Destination(first.Id);
            Assert.NotEqual(destination, await Destination(second.Id));
            var hash = await Pay(destination, "1000000000");
            var confirmed = await LocalMerchant.ReadSend(hash);
            var before = new LocalMerchant(host);
            Assert.Equal(first.Id, await before.RecordObservation(confirmed));
            if (stopAfterBtcpayWrite) await before.SavePayment(confirmed.Id);

            var after = new LocalMerchant(host);
            Assert.Equal(first.Id, await after.RecordObservation(await LocalMerchant.ReadSend(hash)));
            await after.SavePayment(confirmed.Id);
            after.Refresh(first.Id);
            var settled = await AwaitInvoice(client,first.Id,i=>i.Status==InvoiceStatus.Settled);
            Assert.True(DateTimeOffset.UtcNow < settled.ExpirationTime);
            Assert.Equal(1m, settled.PaidAmount);
            Assert.Equal(1, await after.Deliver(first.Id));
            await after.SavePayment(confirmed.Id);
            after.Refresh(first.Id);
            Assert.Equal(0, await after.Deliver(first.Id));
            Assert.Single((await host.InvoiceRepository.GetInvoice(first.Id)).GetPayments(true));
            Assert.Equal(InvoiceStatus.New,(await client.GetInvoice(second.Id)).Status);
            Assert.Empty((await host.InvoiceRepository.GetInvoice(second.Id)).GetPayments(true));
            await client.MarkInvoiceStatus(second.Id, new MarkInvoiceStatusRequest { Status=InvoiceStatus.Settled });
            await AwaitInvoice(client,second.Id,i=>i.Status==InvoiceStatus.Settled);
            Assert.Equal(0, await after.Deliver(second.Id));
            output.WriteLine($"Restart after {(stopAfterBtcpayWrite ? "BTCPay save" : "listener observation")}: {hash}, one stored payment, one delivery. Manual settlement of the equal-price neighbour did not deliver.");
        }

        var partial = await NewInvoice();
        var partialHash = await Pay(await Destination(partial.Id),"250000000");
        var merchant = new LocalMerchant(host);
        var received = await LocalMerchant.ReadSend(partialHash);
        Assert.Equal(partial.Id,await merchant.RecordObservation(received));
        await merchant.SavePayment(received.Id);
        merchant.Refresh(partial.Id);
        var observed = await AwaitInvoice(client,partial.Id,i=>i.PaidAmount==0.25m);
        Assert.NotEqual(InvoiceStatus.Settled,observed.Status);
        Assert.Equal(InvoiceExceptionStatus.PaidPartial,observed.AdditionalStatus);
        Assert.Equal(0,await merchant.Deliver(partial.Id));
        output.WriteLine($"Partial {partialHash}: {observed.PaidAmount} of 1 ATTO, {observed.Status}/{observed.AdditionalStatus}, zero deliveries.");

        var untilExpiry = lateInvoice.ExpirationTime.AddSeconds(2) - DateTimeOffset.UtcNow;
        if (untilExpiry > TimeSpan.Zero) await Task.Delay(untilExpiry);
        await AwaitInvoice(client, lateInvoice.Id, i=>i.Status==InvoiceStatus.Expired);
        var lateHash = await Pay(await Destination(lateInvoice.Id),"1000000000");
        var late = await LocalMerchant.ReadSend(lateHash);
        Assert.Equal(lateInvoice.Id, await merchant.RecordObservation(late));
        await merchant.SavePayment(late.Id);
        merchant.Refresh(lateInvoice.Id);
        var lateState = await AwaitInvoice(client,lateInvoice.Id,i=>i.AdditionalStatus==InvoiceExceptionStatus.PaidLate);
        Assert.Equal(InvoiceStatus.Expired,lateState.Status);
        Assert.Equal(1m,lateState.PaidAmount);
        Assert.Equal(0,await merchant.Deliver(lateInvoice.Id));
        output.WriteLine($"Late {lateHash}: {lateState.PaidAmount} of 1 ATTO, {lateState.Status}/{lateState.AdditionalStatus}, zero deliveries.");
    }
}
