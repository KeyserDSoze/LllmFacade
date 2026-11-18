// Crea .env con una password PostgreSQL casuale se il file non esiste (non lo sovrascrive mai).
import { randomBytes } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const target = join(root, '.env');

if (existsSync(target)) {
  console.log('.env esiste già: non modificato.');
} else {
  const example = readFileSync(join(root, '.env.example'), 'utf8');
  const password = randomBytes(24).toString('hex');
  writeFileSync(target, example.replace(/^POSTGRES_PASSWORD=.*$/m, `POSTGRES_PASSWORD=${password}`), { mode: 0o600 });
  console.log('.env creato con una password PostgreSQL casuale.');
}
