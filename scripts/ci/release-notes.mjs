#!/usr/bin/env node
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const whatsNewPath = join(repoRoot, 'WhatsNew.md');

const args = process.argv.slice(2);
const getArg = (name) => {
    const index = args.indexOf(`--${name}`);
    return index === -1 ? undefined : args[index + 1];
};

const version = getArg('version') ?? process.env.VERSION;
if (!version) {
    console.error('Usage: node scripts/ci/release-notes.mjs --version <x.y.z> [--out <file>]');
    process.exit(1);
}

const markdown = readFileSync(whatsNewPath, 'utf8').replace(/\r\n/g, '\n');
const lines = markdown.split('\n');

const start = lines.findIndex((line) => {
    const prefix = `## ${version}`;
    if (!line.startsWith(prefix)) {
        return false;
    }
    const next = line.charAt(prefix.length);
    return next === '' || next === ' ' || next === '(' || next === '\n';
});

if (start === -1) {
    console.error(`No "## ${version}" section found in ${whatsNewPath}. Add the release notes there first.`);
    process.exit(1);
}

let end = lines.length;
for (let i = start + 1; i < lines.length; i += 1) {
    if (lines[i].startsWith('## ')) {
        end = i;
        break;
    }
}

const section = lines.slice(start, end).join('\n').replace(/\n+---\s*$/, '').trimEnd() + '\n';
const out = getArg('out');
if (out) {
    writeFileSync(out, section, 'utf8');
    console.log(`Wrote ${out} (${section.split('\n').length} lines).`);
} else {
    process.stdout.write(section);
}
