// One version for the whole project, kept in four files:
//   windows/OpenDownloadManager/OpenDownloadManager.csproj  <Version>
//   core/Cargo.toml                                         [workspace.package] version
//   extension/manifest.json                                 "version"
//   package.json                                            "version"
//
//   node scripts/version.mjs            prints the version, fails if the files disagree
//   node scripts/version.mjs 0.2.0      sets it everywhere
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

const FILES = [
  { file: 'windows/OpenDownloadManager/OpenDownloadManager.csproj', re: /(<Version>)([^<]+)(<\/Version>)/ },
  { file: 'core/Cargo.toml', re: /(\[workspace\.package\][^[]*?\nversion = ")([^"]+)(")/ },
  { file: 'extension/manifest.json', re: /("version":\s*")([^"]+)(")/ },
  { file: 'package.json', re: /("version":\s*")([^"]+)(")/ }
];

function read() {
  return FILES.map(({ file, re }) => {
    const m = fs.readFileSync(path.join(root, file), 'utf8').match(re);
    if (!m) throw new Error(`No version found in ${file}`);
    return { file, version: m[2] };
  });
}

const next = process.argv[2];
if (next) {
  if (!/^\d+\.\d+\.\d+$/.test(next)) {
    console.error('Use a plain version like 1.2.3 (Chrome accepts only numbers and dots).');
    process.exit(1);
  }
  for (const { file, re } of FILES) {
    const p = path.join(root, file);
    fs.writeFileSync(p, fs.readFileSync(p, 'utf8').replace(re, `$1${next}$3`));
  }
  // Cargo.lock carries the workspace crates' versions too.
  const lock = path.join(root, 'core/Cargo.lock');
  const text = fs.readFileSync(lock, 'utf8').replace(/(name = "odm-[a-z]+"\nversion = ")[^"]+(")/g, `$1${next}$2`);
  fs.writeFileSync(lock, text);
  console.log(next);
} else {
  const found = read();
  const versions = [...new Set(found.map((f) => f.version))];
  if (versions.length !== 1) {
    for (const f of found) console.error(`${f.version}  ${f.file}`);
    console.error('The versions disagree. Set them with: node scripts/version.mjs <version>');
    process.exit(1);
  }
  console.log(versions[0]);
}
