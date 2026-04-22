import { ChevronLeft, ChevronRight } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useNavHistoryStore } from '@/stores/useNavHistoryStore';

export default function NavButtons() {
  const back = useNavHistoryStore((s) => s.back);
  const forward = useNavHistoryStore((s) => s.forward);
  const canBack = useNavHistoryStore((s) => s.canBack);
  const canForward = useNavHistoryStore((s) => s.canForward);

  return (
    <div className="flex items-center gap-0.5">
      <Button
        variant="ghost"
        size="icon"
        className="h-7 w-7"
        disabled={!canBack}
        onClick={back}
        title="Go back (Alt+←)"
        aria-label="Go back"
      >
        <ChevronLeft className="h-4 w-4" />
      </Button>
      <Button
        variant="ghost"
        size="icon"
        className="h-7 w-7"
        disabled={!canForward}
        onClick={forward}
        title="Go forward (Alt+→)"
        aria-label="Go forward"
      >
        <ChevronRight className="h-4 w-4" />
      </Button>
    </div>
  );
}
