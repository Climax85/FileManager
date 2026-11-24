import { useQuery } from "@tanstack/react-query";
import { FolderOpen } from "lucide-react";
import { Link, useParams } from "react-router-dom";
import { openOrganizationFolder } from "@/api/authorization";
import { MoveResourcePanel } from "@/components/file-manager/move-resource-panel";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export function OrganizationFolderPage() {
  const { folderId = "" } = useParams();
  const folderQuery = useQuery({
    queryKey: ["authorization", "folders", folderId],
    queryFn: () => openOrganizationFolder(folderId),
    enabled: folderId.length > 0,
    retry: false,
  });

  return (
    <div className="space-y-6">
      <Link to="/file-manager" className="text-sm text-[var(--color-primary)]">← File Manager</Link>
      {folderQuery.isPending ? <p>Opening folder…</p> : null}
      {folderQuery.isError ? (
        <Card>
          <CardHeader><CardTitle>Folder unavailable</CardTitle></CardHeader>
          <CardContent><CardDescription>The folder does not exist or you do not have access.</CardDescription></CardContent>
        </Card>
      ) : null}
      {folderQuery.data ? (
        <div className="space-y-4">
          <div className="flex items-center gap-3">
            <FolderOpen className="size-6 text-[var(--color-primary)]" />
            <div>
              <h1 className="font-display text-2xl font-semibold">{folderQuery.data.name}</h1>
              {folderQuery.data.isRoot ? <p className="text-sm text-[var(--color-muted-foreground)]">Organization root folder</p> : null}
            </div>
          </div>
          <Card>
            <CardHeader><CardTitle>Folder contents</CardTitle></CardHeader>
            <CardContent><CardDescription>This folder is open. Child folders and files will appear here.</CardDescription></CardContent>
          </Card>
          {!folderQuery.data.isRoot ? (
            <MoveResourcePanel
              resourceType="Folder"
              resourceId={folderQuery.data.id}
              authorizationToken={folderQuery.data.authorizationToken}
            />
          ) : null}
        </div>
      ) : null}
    </div>
  );
}
