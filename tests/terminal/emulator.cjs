const { Terminal } = require('@xterm/headless');
const readline = require('node:readline');

let terminal;

function text(buffer) {
  const rows = [];
  for (let index = 0; index < buffer.length; index++) {
    rows.push(buffer.getLine(index).translateToString(true));
  }
  return rows.join('\n').trimEnd();
}

async function main() {
  for await (const line of readline.createInterface({ input: process.stdin })) {
    const command = JSON.parse(line);
    switch (command.action) {
      case 'open':
        terminal = new Terminal({
          cols: command.columns,
          rows: command.rows,
          scrollback: 2000,
          allowProposedApi: true
        });
        break;
      case 'write':
        await new Promise(resolve => terminal.write(command.data, resolve));
        break;
      case 'resize':
        terminal.resize(command.columns, command.rows);
        break;
      case 'snapshot':
        break;
      default:
        throw new Error(`Unknown emulator action: ${command.action}`);
    }
    console.log(JSON.stringify({
      alternate: terminal.buffer.active.type === 'alternate',
      normal: text(terminal.buffer.normal),
      active: text(terminal.buffer.active)
    }));
  }
  terminal?.dispose();
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
