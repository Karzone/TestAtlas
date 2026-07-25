#!/usr/bin/env node
// Minimal example MCP client for TestAtlas — spawns `testatlas-mcp <db>`, speaks
// JSON-RPC 2.0 over stdio, calls one tool, and prints the server's raw answer.
// This is exactly what an AI agent does under the hood.
//
//   node mcp-ask.mjs <db> impact <class|method|step|endpoint> <value>
//   node mcp-ask.mjs <db> find  <query>      (scenarios)
//   node mcp-ask.mjs <db> steps <query>      (step definitions)
import { spawn } from 'node:child_process';

const [db, verb, ...rest] = process.argv.slice(2);
if (!db || !verb) { console.error('usage: mcp-ask.mjs <db> <impact|find|steps> ...'); process.exit(1); }

let name, args;
if (verb === 'impact') { name = 'impact'; args = { target: rest[0], value: rest.slice(1).join(' ') }; }
else if (verb === 'find') { name = 'search_scenarios'; args = { query: rest.join(' ') }; }
else if (verb === 'steps') { name = 'search_steps'; args = { query: rest.join(' ') }; }
else { console.error('unknown verb', verb); process.exit(1); }

const mcp = spawn('testatlas-mcp', [db], { stdio: ['pipe', 'pipe', 'inherit'] });
const send = (o) => mcp.stdin.write(JSON.stringify(o) + '\n');

send({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2024-11-05', capabilities: {}, clientInfo: { name: 'mcp-ask', version: '1' } } });
send({ jsonrpc: '2.0', method: 'notifications/initialized' });
send({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name, arguments: args } });

let buf = '';
mcp.stdout.on('data', (d) => {
  buf += d.toString();
  let nl;
  while ((nl = buf.indexOf('\n')) >= 0) {
    const line = buf.slice(0, nl).trim(); buf = buf.slice(nl + 1);
    if (!line) continue;
    let msg; try { msg = JSON.parse(line); } catch { continue; }
    if (msg.id === 2) {
      const text = (msg.result?.content || []).filter(c => c.type === 'text').map(c => c.text).join('\n');
      try { console.log(JSON.stringify(JSON.parse(text), null, 2)); }
      catch { console.log(text); }
      mcp.stdin.end(); mcp.kill(); process.exit(0);
    }
  }
});
mcp.on('exit', () => process.exit(0));
