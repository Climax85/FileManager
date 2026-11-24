import { useQuery } from "@tanstack/react-query";
import { FolderOpen, FolderTree } from "lucide-react";
import { Link } from "react-router-dom";
import { getOrganizationRoot } from "@/api/authorization";
import { SharingPanel } from "@/components/file-manager/sharing-panel";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";

export function OrganizationRootPage() {
  const rootQuery = useQuery({
    queryKey: ["authorization", "organization", "root"],
    queryFn: () => getOrganizationRoot(),
    retry: false,
  });

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <FolderTree className="size-6 text-[var(--color-primary)]" />
        <div>
          <h1 className="font-display text-2xl font-semibold">File Manager</h1>
          <p className="text-sm text-[var(--color-muted-foreground)]">Your organization’s shared files and folders.</p>
        </div>
      </div>

      {rootQuery.isPending ? <p>Loading organization root…</p> : null}
      {rootQuery.isError ? (
        <Card>
          <CardHeader><CardTitle>No accessible organization root</CardTitle></CardHeader>
          <CardContent>
            <CardDescription>
              The root is unavailable or you do not have access. Contact an organization administrator if you believe this is incorrect.
            </CardDescription>
          </CardContent>
        </Card>
      ) : null}
      {rootQuery.data ? (
        <>
          <Card interactive className="max-w-xl">
            <CardHeader>
              <CardTitle>{rootQuery.data.name}</CardTitle>
              <CardDescription>Organization root folder</CardDescription>
            </CardHeader>
            <CardContent>
              <Link
                to={`/file-manager/folders/${rootQuery.data.id}`}
                className="inline-flex items-center gap-2 rounded-md bg-[var(--color-primary)] px-4 py-2 text-sm font-medium text-[var(--color-primary-foreground)]"
              >
                <FolderOpen className="size-4" /> Open root folder
              </Link>
            </CardContent>
          </Card>
          <SharingPanel
            resource={{ resourceType: "Folder", resourceId: rootQuery.data.id }}
            organizationId={rootQuery.data.organizationId}
            authorizationToken={rootQuery.data.authorizationToken}
          />
        </>
      ) : null}
    </div>
  );
}
