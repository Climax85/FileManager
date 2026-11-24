import { apiFetch } from "@/lib/api-client";

export type OrganizationFolder = {
  id: string;
  organizationId: string;
  name: string;
  isRoot: boolean;
  createdAt: string;
  authorizationToken: string;
  parentId?: string | null;
  canEdit: boolean;
};

export type FolderChildrenPage = {
  items: OrganizationFolder[];
  nextCursor: string | null;
  authorizationToken: string;
};

function authorizationHeaders(token?: string) {
  return token ? { "X-Authz-Token": token } : undefined;
}

export function getOrganizationRoot(authorizationToken?: string): Promise<OrganizationFolder> {
  return apiFetch<OrganizationFolder>("/api/v1/authorization/organization/root", {
    headers: authorizationHeaders(authorizationToken),
  });
}

export function openOrganizationFolder(folderId: string, authorizationToken?: string): Promise<OrganizationFolder> {
  return apiFetch<OrganizationFolder>(`/api/v1/authorization/folders/${encodeURIComponent(folderId)}`, {
    headers: authorizationHeaders(authorizationToken),
  });
}

export function listFolderChildren(folderId: string, pageSize = 50, cursor?: string | null, authorizationToken?: string) {
  const query = new URLSearchParams({ pageSize: String(pageSize) });
  if (cursor) query.set("cursor", cursor);
  return apiFetch<FolderChildrenPage>(
    `/api/v1/authorization/folders/${encodeURIComponent(folderId)}/children?${query}`,
    { headers: authorizationHeaders(authorizationToken) },
  );
}

export function createFolder(input: { parentId: string; name: string; authorizationToken?: string }) {
  return apiFetch<OrganizationFolder>("/api/v1/authorization/folders", {
    method: "POST",
    headers: authorizationHeaders(input.authorizationToken),
    body: JSON.stringify(input),
  });
}

export function renameFolder(input: { folderId: string; name: string; authorizationToken?: string }) {
  return apiFetch<OrganizationFolder>(`/api/v1/authorization/folders/${encodeURIComponent(input.folderId)}`, {
    method: "PATCH",
    headers: authorizationHeaders(input.authorizationToken),
    body: JSON.stringify({ name: input.name, authorizationToken: input.authorizationToken }),
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
