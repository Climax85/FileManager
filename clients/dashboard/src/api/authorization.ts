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
