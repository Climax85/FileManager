import { useState } from "react";
import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { FolderOpen } from "lucide-react";
import { Link, useParams } from "react-router-dom";
import { toast } from "sonner";
import {
  createFolder,
  listFolderChildren,
  openOrganizationFolder,
  renameFolder,
} from "@/api/authorization";
import { MoveResourcePanel } from "@/components/file-manager/move-resource-panel";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";

export function OrganizationFolderPage() {
  const { folderId = "" } = useParams();
  const queryClient = useQueryClient();
  const [newFolderName, setNewFolderName] = useState("");
  const [rename, setRename] = useState(false);
  const folderQuery = useQuery({
    queryKey: ["authorization", "folders", folderId],
    queryFn: () => openOrganizationFolder(folderId),
    enabled: folderId.length > 0,
    retry: false,
  });
  const folder = folderQuery.data;
  const childrenQuery = useInfiniteQuery({
    queryKey: ["authorization", "folders", folderId, "children"],
    queryFn: ({ pageParam }) => listFolderChildren(folderId, 50, pageParam, folder?.authorizationToken),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor,
    enabled: !!folder,
  });

  const createMutation = useMutation({
    mutationFn: createFolder,
    onSuccess: async () => {
      setNewFolderName("");
      await queryClient.invalidateQueries({ queryKey: ["authorization", "folders", folderId, "children"] });
    },
    onError: () => toast.error("Folder creation failed"),
  });
  const renameMutation = useMutation({
    mutationFn: renameFolder,
    onSuccess: async () => {
      setRename(false);
      await queryClient.invalidateQueries({ queryKey: ["authorization", "folders", folderId] });
    },
    onError: () => toast.error("Folder rename failed"),
  });
  const children = childrenQuery.data?.pages.flatMap((page) => page.items) ?? [];

  function submitCreate() {
    const name = newFolderName.trim();
    if (name) createMutation.mutate({ parentId: folderId, name, authorizationToken: folder?.authorizationToken });
  }

  function submitRename() {
    const name = newFolderName.trim();
    if (name) renameMutation.mutate({ folderId, name, authorizationToken: folder?.authorizationToken });
  }

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
      {folder ? (
        <div className="space-y-4">
          <div className="flex items-center gap-3">
            <FolderOpen className="size-6 text-[var(--color-primary)]" />
            <div>
              <h1 className="font-display text-2xl font-semibold">{folder.name}</h1>
              {folder.isRoot ? <p className="text-sm text-[var(--color-muted-foreground)]">Organization root folder</p> : null}
            </div>
          </div>
          {folder.canEdit ? (
            <div className="flex flex-wrap items-end gap-2">
              <label className="grid gap-1 text-sm">
                {rename ? "Folder name" : "New folder name"}
                <Input value={newFolderName} onChange={(event) => setNewFolderName(event.target.value)} />
              </label>
              {rename ? (
                <>
                  <Button disabled={renameMutation.isPending} onClick={submitRename}>Save folder name</Button>
                  <Button variant="outline" onClick={() => setRename(false)}>Cancel</Button>
                </>
              ) : (
                <>
                  <Button disabled={createMutation.isPending} onClick={submitCreate}>Create folder</Button>
                  {!folder.isRoot ? <Button variant="outline" onClick={() => { setRename(true); setNewFolderName(folder.name); }}>Rename folder</Button> : null}
                </>
              )}
            </div>
          ) : null}
          <Card>
            <CardHeader><CardTitle>Folder contents</CardTitle></CardHeader>
            <CardContent className="space-y-3">
              {children.length === 0 && !childrenQuery.isPending ? <CardDescription>No child folders.</CardDescription> : null}
              <ul className="divide-y divide-[var(--color-border)]">
                {children.map((child) => (
                  <li key={child.id} className="py-2">
                    <Link to={`/file-manager/folders/${child.id}`} className="text-[var(--color-primary)]">{child.name}</Link>
                  </li>
                ))}
              </ul>
              {childrenQuery.hasNextPage ? (
                <Button variant="outline" disabled={childrenQuery.isFetchingNextPage} onClick={() => childrenQuery.fetchNextPage()}>
                  Load more folders
                </Button>
              ) : null}
            </CardContent>
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
