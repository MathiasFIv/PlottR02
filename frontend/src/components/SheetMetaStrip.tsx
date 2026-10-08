import type { SheetMetadata } from '../types';

type SheetMetaStripProps = {
  metadata: SheetMetadata;
};

/**
 * Workbook-level ARM metadata strip (D3 / D4 — read-for-display approved
 * 2026-10-07). One logical value per field: the server already collapsed the
 * physically-repeated row 5/6/10/11/12/20 cells into `metadata`.
 *
 * Only non-null, non-blank fields render — a workbook missing e.g. the rating
 * date shows the remaining fields instead of an empty label. Display-only: the
 * values are not ISO (`16Jun2026`), they are shown exactly as the file has
 * them.
 */
export function SheetMetaStrip({ metadata }: SheetMetaStripProps) {
  const fields: Array<{ label: string; value: string | null }> = [
    { label: 'Crop', value: metadata.crop },
    { label: 'Rating date', value: metadata.ratingDate },
    { label: 'Rating type', value: metadata.ratingType },
    { label: 'Unit', value: metadata.unit },
    { label: 'Sample size', value: metadata.sampleSize },
    { label: '# subsamples', value: metadata.subsampleCount },
  ];
  const visible = fields.filter(
    (field): field is { label: string; value: string } =>
      typeof field.value === 'string' && field.value.trim() !== '',
  );

  if (visible.length === 0) return null;

  return (
    <dl className="sheet-meta">
      {visible.map((field) => (
        <div className="sheet-meta__item" key={field.label}>
          <dt className="sheet-meta__label">{field.label}</dt>
          <dd className="sheet-meta__value">{field.value}</dd>
        </div>
      ))}
    </dl>
  );
}
