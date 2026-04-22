import { XCircle } from 'lucide-react';
import { useSessionStore } from '@/stores/useSessionStore';

export default function IncompatibleBanner() {
 const diagnostics = useSessionStore((s) => s.schemaDiagnostics);

 return (
 <div
 role="alert"
 className="flex items-start gap-3 border-b border-destructive/50 bg-destructive/10 px-4 py-2
 text-density-sm text-destructive"
 >
 <XCircle className="mt-0.5 h-4 w-4 shrink-0" aria-hidden="true" />
 <div className="min-w-0 flex-1">
 <p className="font-semibold">Schema incompatible</p>
 <p className="mt-0.5 text-[11px] opacity-90">
 This database cannot be opened with the loaded schema DLLs. Close this session and load
 a compatible schema to continue.
 </p>
 {diagnostics && diagnostics.length > 0 && (
 <ul className="mt-1 list-disc pl-4 text-[11px] opacity-80">
 {diagnostics.slice(0, 3).map((d, i) => (
 <li key={i}>
 <span className="font-semibold">{d.componentName}</span> — {d.kind}
 </li>
 ))}
 </ul>
 )}
 </div>
 </div>
 );
}
