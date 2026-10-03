import { createReadStream, createWriteStream } from 'node:fs';
import { Readable } from 'node:stream';
import { TransformStream } from 'node:stream/web';
import { createInterface } from 'node:readline';
import { createGunzip } from 'node:zlib';
import { once } from 'node:events';
import { GFFTransformer } from '@gmod/gff';
import VCF from '@gmod/vcf';

const [mode, input, output] = process.argv.slice(2);
if (!['gff', 'vcf'].includes(mode) || !input || !output) throw new Error('Expected gff|vcf input output');
const source = input.endsWith('.gz') ? createReadStream(input).pipe(createGunzip()) : createReadStream(input);
const destination = createWriteStream(output, { encoding: 'utf8', flags: 'wx' });
async function write(value) {
  if (!destination.write(JSON.stringify(value) + '\n')) await once(destination, 'drain');
}
if (mode === 'gff') {
  const seen = new Set();
  async function visit(group) {
    for (const feature of group) {
      if (!feature.seq_id || !feature.type || !Number.isInteger(feature.start) ||
          !Number.isInteger(feature.end) || feature.start < 1 || feature.end < feature.start)
        throw new Error('Invalid GFF feature coordinates or missing seq_id/type');
      const key = JSON.stringify([feature.seq_id, feature.type, feature.start, feature.end, feature.attributes]);
      if (!seen.has(key)) {
        seen.add(key);
        await write({ SeqId: feature.seq_id, Type: feature.type, Start: feature.start, End: feature.end,
          Strand: feature.strand, Id: feature.attributes?.ID?.[0] ?? '', Parents: feature.attributes?.Parent ?? [],
          Phase: feature.phase });
      }
      for (const child of feature.child_features ?? []) await visit(child);
    }
  }
  const parsed = Readable.toWeb(source).pipeThrough(new TransformStream(new GFFTransformer({ parseSequences: false })));
  for await (const item of parsed) if (Array.isArray(item)) await visit(item);
  if (!seen.size) throw new Error('GFF contains no valid features');
} else {
  const lines = createInterface({ input: source, crlfDelay: Infinity });
  const header = [];
  let parser;
  for await (const line of lines) {
    if (!line) continue;
    if (line.startsWith('#')) { header.push(line); continue; }
    parser ??= new VCF({ header: header.join('\n') });
    const variant = parser.parseLine(line);
    if (!variant.ALT?.length || variant.ALT.every(v => v === '.' || v === '<NON_REF>')) continue;
    await write({ SeqId: variant.CHROM, Position: variant.POS, Id: variant.ID?.join(',') ?? '',
      Ref: variant.REF, Alt: variant.ALT.join(','), Frequency: variant.INFO.AF?.[0] ?? null,
      End: variant.INFO.END?.[0] ?? variant.POS + variant.REF.length - 1,
      Filter: Array.isArray(variant.FILTER) ? variant.FILTER.join(';') : variant.FILTER ?? 'unknown' });
  }
  if (!header.some(line => line.startsWith('##fileformat=VCFv')) || !header.some(line => line.startsWith('#CHROM\t')))
    throw new Error('VCF requires fileformat and #CHROM headers');
  parser ??= new VCF({ header: header.join('\n') });
}
destination.end();
await once(destination, 'finish');
