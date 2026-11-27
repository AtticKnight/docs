// Disposable LOCAL-only ledger used by the BTCPay checkout test.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { AttoMnemonic, toAttoIndex, toPrivateKey, toSeedAsync } from '@attocash/commons-core';
import { AttoNodeMockAsyncBuilder, AttoWorkerMockAsyncBuilder } from '@attocash/commons-test';
import { executeAttoOperation } from '../dist/nodes/Atto/operations.js';

const phrase = await AttoMnemonic.generate();
const genesis = await toPrivateKey(await toSeedAsync(phrase), toAttoIndex(0));
const node = await new AttoNodeMockAsyncBuilder(genesis).image('ghcr.io/attocash/node:live').mysqlImage('mysql:8.4').build();
const worker = await new AttoWorkerMockAsyncBuilder().image('ghcr.io/attocash/work-server:cpu').build();
await node.start();
await worker.start();

const credentials = keyIndex => ({
  nodeUrl: node.baseUrl, workerUrl: worker.baseUrl, keyIndex,
  walletMaterialType: 'mnemonic', walletSecret: phrase.phrase,
});
const context = {
  getNode: () => ({ name: 'LOCAL checkout fixture' }),
  getMode: () => 'manual',
  getWorkflowStaticData: () => ({}),
  helpers: {
    async httpRequest(options) {
      const response = await fetch(options.url, { method: options.method, headers: options.headers,
        body: options.body, signal: options.abortSignal || AbortSignal.timeout(30000) });
      if (options.encoding === 'stream') {
        if (!response.ok) throw new Error('HTTP ' + response.status);
        return response.body;
      }
      const body = await response.text();
      if (!response.ok && !options.ignoreHttpStatusErrors) throw new Error('HTTP ' + response.status);
      return options.returnFullResponse ? { statusCode: response.status, headers: Object.fromEntries(response.headers), body } : body;
    }
  }
};
const derive = async index => executeAttoOperation(context, 'deriveAddress', {secretSource:'credentials'}, credentials(index));
const genesisAddress = await derive(0);
const root = new URL(node.baseUrl);
assert.ok(['127.0.0.1','localhost','[::1]'].includes(root.hostname));
const acct = await fetch(new URL('/accounts/' + genesisAddress.publicKey, root)).then(r => r.json());
assert.equal(acct.network, 'LOCAL');
const recipients = new Map();
let next = 1;
const server = createServer(async (req, res) => {
  const send = (status, body) => { res.writeHead(status, {'Content-Type':'application/json'}); res.end(JSON.stringify(body)); };
  try {
    if (req.method==='GET' && req.url==='/health') return send(200, {node:root.href,network:acct.network});
    if (req.method==='POST' && req.url==='/receivers') {
      const idx = next++;
      const receiver = await derive(idx);
      recipients.set(receiver.address, receiver.publicKey);
      return send(201, {address:receiver.address,publicKey:receiver.publicKey});
    }
    if (req.method==='POST' && req.url==='/payments') {
      const chunks=[];
      for await (const part of req) chunks.push(part);
      const {destination,raw}=JSON.parse(Buffer.concat(chunks).toString());
      assert.ok(recipients.has(destination));
      assert.ok(['1000000000','250000000'].includes(raw));
      const tx = await executeAttoOperation(context,'sendTransaction',{
        secretSource:'credentials',destinationAddress:destination,amount:raw,amountUnit:'RAW'
      },credentials(0));
      assert.equal(tx.status,'published');
      assert.equal(tx.destinationAddress,destination);
      return send(201,{hash:tx.hash});
    }
    send(404,{error:'Unknown fixture route'});
  } catch (err) { send(500,{error:err.message}); }
});
server.listen(19345,'127.0.0.1',()=>console.log('LOCAL Atto node and work service ready for merchant checkout.'));
async function close() {
  server.close();
  await Promise.allSettled([node.close(),worker.close()]);
  process.exit(0);
}
process.on('SIGTERM',close);
process.on('SIGINT',close);
