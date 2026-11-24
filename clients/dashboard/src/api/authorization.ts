import { apiFetch } from "@/lib/api-client";

export type OrganizationFolder = {
  id: string;
  organizationId: string;
  name: string;
  isRoot: boolean;
  createdAt: string;
  authorizationToken: string;
};

export function getOrganizationRoot(authorizationToken?: string): Promise<OrganizationFolder> {
  return apiFetch<OrganizationFolder>("/api/v1/authorization/organization/root", {
    headers: authorizationToken ? { "X-Authz-Token": authorizationToken } : undefined,
  });
}

export function openOrganizationFolder(folderId: string, authorizationToken?: string): Promise<OrganizationFolder> {
  return apiFetch<OrganizationFolder>(`/api/v1/authorization/folders/${encodeURIComponent(folderId)}`, {
    headers: authorizationToken ? { "X-Authz-Token": authorizationToken } : undefined,
  });
}

export type MoveResourceResult = {
  resourceType: "Folder" | "File";
  resourceId: string;
  parentFolderId: string;
  operationId: string;
  synchronizationStatus: "Pending" | "Confirmed";
  authorizationToken: string;
};

export function moveResource(
  resourceType: "Folder" | "File",
  resourceId: string,
  destinationFolderId: string,
  authorizationToken: string,
): Promise<MoveResourceResult> {
  return apiFetch<MoveResourceResult>(
    `/api/v1/authorization/resources/${resourceType}/${encodeURIComponent(resourceId)}/move`,
    {
      method: "POST",
      headers: { "X-Authz-Token": authorizationToken },
      body: JSON.stringify({ destinationFolderId }),
    },
  );
}
