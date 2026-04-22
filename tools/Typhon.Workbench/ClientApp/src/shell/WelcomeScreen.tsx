import { useState } from 'react';
import { Clock, Database, FileSearch, Plug } from 'lucide-react';
import { Button } from '@/components/ui/button';
import ConnectDialog, { type ConnectTab } from './dialogs/ConnectDialog';

export default function WelcomeScreen() {
 const [dialogOpen, setDialogOpen] = useState(false);
 const [initialTab, setInitialTab] = useState<ConnectTab>('open');

 const openDialog = (tab: ConnectTab) => {
 setInitialTab(tab);
 setDialogOpen(true);
 };

 return (
 <div className="flex h-full flex-col items-center justify-center gap-8 bg-background">
 <div className="text-center">
 <h1 className="mb-1 text-xl font-semibold text-foreground">Typhon Workbench</h1>
 <p className="text-density-sm text-muted-foreground">
 Open a database, trace file, or attach to a running engine
 </p>
 </div>

 <div className="flex gap-3">
 <Button
 variant="outline"
 className="flex h-auto flex-col items-center gap-2 px-6 py-4 text-density-sm"
 onClick={() => openDialog('open')}
 >
 <Database className="h-5 w-5" />
 <span>Open .typhon File</span>
 </Button>

 <Button
 variant="outline"
 className="flex h-auto flex-col items-center gap-2 px-6 py-4 text-density-sm"
 onClick={() => openDialog('cached')}
 >
 <FileSearch className="h-5 w-5" />
 <span>Open .typhon-trace</span>
 </Button>

 <Button
 variant="outline"
 className="flex h-auto flex-col items-center gap-2 px-6 py-4 text-density-sm"
 onClick={() => openDialog('attach')}
 >
 <Plug className="h-5 w-5" />
 <span>Attach to Engine</span>
 </Button>

 <Button
 variant="outline"
 className="flex h-auto flex-col items-center gap-2 px-6 py-4 text-density-sm"
 onClick={() => openDialog('recent')}
 >
 <Clock className="h-5 w-5" />
 <span>Recent Files</span>
 </Button>
 </div>

 <ConnectDialog open={dialogOpen} initialTab={initialTab} onOpenChange={setDialogOpen} />
 </div>
 );
}
