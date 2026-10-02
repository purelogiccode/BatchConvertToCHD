#!/usr/bin/env node
import { appendFileSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');
const csprojPath = join(repoRoot, 'BatchConvertToCHD', 'BatchConvertToCHD.csproj');

const args = process.argv.slice(2);
const getArg = (name) => {
    const index = args.indexOf(`--${name}`);
    return index === -1 ? undefined : args[index + 1];
};

const csproj = readFileSync(csprojPath, 'utf8');
const match = csproj.match(/<AssemblyVersion>([^<]+)<\/AssemblyVersion>/);
if (!match) {
    console.error(`Could not find <AssemblyVersion> in ${csprojPath}`);
    process.exit(1);
}
const version = match[1].trim();

const tag = getArg('tag');
if (tag !== undefined) {
    const expected = `release_${version}`;
    if (tag !== expected) {
        console.error(`Tag "${tag}" does not match the project version. Expected "${expected}" (update BatchConvertToCHD.csproj or the tag).`);
        process.exit(1);
    }
    console.log(`Tag "${tag}" matches BatchConvertToCHD ${version}.`);
}

if (args.includes('--github-output')) {
    const outputFile = process.env.GITHUB_OUTPUT;
    if (!outputFile) {
        console.error('--github-output was requested but GITHUB_OUTPUT is not set.');
        process.exit(1);
    }
    appendFileSync(outputFile, `version=${version}\n`);
}

console.log(version);
