import type { IDockviewPanelProps } from 'dockview-react';
import { Check, X } from 'lucide-react';
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from '@/components/ui/table';
import { useSchemaInspectorStore } from '@/stores/useSchemaInspectorStore';
import { useIndexesForComponent } from '@/hooks/schema/useIndexesForComponent';

/**
 * Index panel for the Schema Inspector. Lists indexes covering fields of the focused component.
 * Row click highlights the field in the Schema Layout view via the shared selection store.
 */
export default function SchemaIndexPanel(_props: IDockviewPanelProps) {
  const selectedType = useSchemaInspectorStore((s) => s.selectedComponentType);
  const selectField = useSchemaInspectorStore((s) => s.selectField);
  const { indexes, isLoading, isError } = useIndexesForComponent(selectedType);

  if (!selectedType) {
    return (
      <div className="flex h-full items-center justify-center bg-background">
        <p className="text-[12px] text-muted-foreground">No component selected.</p>
      </div>
    );
  }

  return (
    <div className="flex h-full w-full flex-col overflow-hidden bg-background">
      <div className="flex items-center gap-2 border-b border-border px-3 py-1.5">
        <h3 className="font-mono text-[12px] font-semibold text-foreground">{selectedType}</h3>
        <span className="text-[11px] text-muted-foreground">indexes covering this component</span>
        <span className="ml-auto text-[11px] text-muted-foreground">{indexes.length}</span>
      </div>

      <div className="flex-1 overflow-auto">
        {isError && <p className="p-3 text-[12px] text-destructive">Failed to load indexes.</p>}
        {isLoading && <p className="p-3 text-[12px] text-muted-foreground">Loading indexes…</p>}
        {!isLoading && indexes.length === 0 && (
          <div className="p-3 text-[12px] text-muted-foreground">
            <p>No indexes cover fields of this component.</p>
            <p className="mt-1 text-[11px]">
              Indexes are declared with <code className="rounded bg-muted px-1">[Index]</code> on the C# struct field.
            </p>
          </div>
        )}
        {indexes.length > 0 && (
          <Table className="text-[12px]">
            <TableHeader>
              <TableRow>
                <TableHead className="py-1 text-[11px]">Field</TableHead>
                <TableHead className="py-1 text-[11px]">Offset</TableHead>
                <TableHead className="py-1 text-[11px]">Size</TableHead>
                <TableHead className="py-1 text-[11px]">Multi</TableHead>
                <TableHead className="py-1 text-[11px]">Type</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {indexes.map((ix) => (
                <TableRow
                  key={`${ix.fieldName}-${ix.fieldOffset}`}
                  className="cursor-pointer hover:bg-accent"
                  onClick={() => selectField(ix.fieldName)}
                  title="Click to highlight this field in Component Layout"
                  data-testid="index-row"
                  data-field-name={ix.fieldName}
                >
                  <TableCell className="py-1 font-mono">{ix.fieldName}</TableCell>
                  <TableCell className="py-1 text-right font-mono tabular-nums">
                    {ix.fieldOffset} (0x{ix.fieldOffset.toString(16).toUpperCase()})
                  </TableCell>
                  <TableCell className="py-1 text-right tabular-nums">{ix.fieldSize}B</TableCell>
                  <TableCell className="py-1">
                    {ix.allowsMultiple ? (
                      <Check className="h-3.5 w-3.5 text-emerald-400" aria-label="Allows multiple" />
                    ) : (
                      <X className="h-3.5 w-3.5 text-muted-foreground" aria-label="Unique" />
                    )}
                  </TableCell>
                  <TableCell className="py-1 text-[11px] text-muted-foreground">{ix.indexType}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </div>
    </div>
  );
}
