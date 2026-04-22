import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';

export default function AttachTab() {
 return (
 <div className="flex h-full flex-col items-center justify-center gap-4">
 <div className="w-80 space-y-2">
 <label className="text-density-sm text-muted-foreground">Engine endpoint</label>
 <Input placeholder="localhost:5100" disabled className="" />
 <p className="text-[10px] text-muted-foreground">
 Attach to a live engine over TCP. Enabled once Phase 5 wires attach transport.
 </p>
 </div>
 <Button disabled className="text-density-sm" title="Coming in a later phase">
 Attach (coming soon)
 </Button>
 </div>
 );
}
