import { apiFetch } from "@/lib/api-client";

export type ShareableResource = {
  resourceType: "File" | "Folder";
  resourceId: string;
};

export type GrantRelation = "Viewer" | "Editor" | "Manager";
export type GrantSubjectType = "User" | "Organization";
export type ResourceGrantStatus = "Pending" | "Active" | "RevocationPending" | "Revoked";

export type ResourceGrant = {
  id: string;
  resourceType: "File" | "Folder";
  resourceId: string;
  subjectType: GrantSubjectType;
  subjectId: string;
  relation: GrantRelation;
  status: ResourceGrantStatus;
  authorizationToken: string;
  createdAt: string;
  confirmedAt: string | null;
};

export type EffectiveAccessSource = {
  grantId: string | null;
  source: "Direct" | "Inherited" | "Organization";
  resourceType: "File" | "Folder";
  resourceId: string;
  relation: GrantRelation;
  subjectType: GrantSubjectType;
  subjectId: string;
};

export type EffectiveAccess = {
  resourceType: "File" | "Folder";
  resourceId: string;
  subjectId: string;
  access: "None" | "Viewer" | "Editor" | "Manager";
  sources: EffectiveAccessSource[];
  authorizationToken: string;
};

export type CreateGrantInput = {
  subjectType: GrantSubjectType;
  subjectId: string;
  relation: GrantRelation;
  confirmOrganizationWide: boolean;
};

function resourcePath(resource: ShareableResource, suffix: string): string {
  return `/api/v1/authorization/resources/${resource.resourceType}/${encodeURIComponent(resource.resourceId)}/${suffix}`;
}

function tokenHeader(authorizationToken?: string): HeadersInit | undefined {
  return authorizationToken ? { "X-Authz-Token": authorizationToken } : undefined;
}

export async function listResourceGrants(
  resource: ShareableResource,
  authorizationToken?: string,
): Promise<{ grants: ResourceGrant[]; authorizationToken: string }> {
  return apiFetch(resourcePath(resource, "grants"), { headers: tokenHeader(authorizationToken) });
}

export async function createResourceGrant(
  resource: ShareableResource,
  input: CreateGrantInput,
  authorizationToken?: string,
): Promise<ResourceGrant> {
  return apiFetch(resourcePath(resource, "grants"), {
    method: "POST",
    body: JSON.stringify(input),
    headers: tokenHeader(authorizationToken),
  });
}

export async function revokeResourceGrant(grantId: string, authorizationToken?: string): Promise<ResourceGrant> {
  return apiFetch(`/api/v1/authorization/grants/${encodeURIComponent(grantId)}`, {
    method: "DELETE",
    headers: tokenHeader(authorizationToken),
  });
}

export async function getEffectiveAccess(
  resource: ShareableResource,
  subjectId: string,
  authorizationToken?: string,
): Promise<EffectiveAccess> {
  return apiFetch(resourcePath(resource, `effective-access?subjectId=${encodeURIComponent(subjectId)}`), {
    headers: tokenHeader(authorizationToken),
  });
}
