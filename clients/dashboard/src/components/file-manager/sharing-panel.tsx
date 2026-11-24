import { useId, useState, type FormEvent } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ShieldCheck, UsersRound } from "lucide-react";
import { toast } from "sonner";
import {
  createResourceGrant,
  getEffectiveAccess,
  listResourceGrants,
  revokeResourceGrant,
  type CreateGrantInput,
  type GrantRelation,
  type GrantSubjectType,
  type ResourceGrant,
  type ShareableResource,
} from "@/api/sharing";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Dialog,
  DialogBody,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

export type SharingPanelProps = {
  resource: ShareableResource;
  organizationId: string;
  authorizationToken: string;
};

export function SharingPanel({ resource, organizationId, authorizationToken }: SharingPanelProps) {
  const id = useId();
  const queryClient = useQueryClient();
  const [token, setToken] = useState(authorizationToken);
  const [subjectType, setSubjectType] = useState<GrantSubjectType>("User");
  const [subjectId, setSubjectId] = useState("");
  const [relation, setRelation] = useState<GrantRelation>("Viewer");
  const [confirmedOrganizationWide, setConfirmedOrganizationWide] = useState(false);
  const [showOrganizationWarning, setShowOrganizationWarning] = useState(false);
  const [revokeTarget, setRevokeTarget] = useState<ResourceGrant | null>(null);
  const [effectiveSubject, setEffectiveSubject] = useState("");
  const grantsQuery = useQuery({
    queryKey: ["authorization", "sharing", resource.resourceType, resource.resourceId, token],
    queryFn: () => listResourceGrants(resource, token),
    retry: false,
  });
  const effectiveQuery = useQuery({
    queryKey: ["authorization", "effective-access", resource.resourceType, resource.resourceId, effectiveSubject, token],
    queryFn: () => getEffectiveAccess(resource, effectiveSubject, token),
    enabled: effectiveSubject.length > 0,
    retry: false,
  });
  const invalidate = () => queryClient.invalidateQueries({
    queryKey: ["authorization", "sharing", resource.resourceType, resource.resourceId],
  });
  const grantMutation = useMutation({
    mutationFn: ({ input, atLeastAsFresh }: { input: CreateGrantInput; atLeastAsFresh: string }) =>
      createResourceGrant(resource, input, atLeastAsFresh),
    onSuccess: (grant) => {
      setToken(grant.authorizationToken);
      setConfirmedOrganizationWide(false);
      setShowOrganizationWarning(false);
      toast.success("Access granted and synchronized.");
      void invalidate();
    },
    onError: () => toast.error("Access was not granted. Pending grants remain ineffective."),
  });
  const revocationMutation = useMutation({
    mutationFn: ({ grantId, atLeastAsFresh }: { grantId: string; atLeastAsFresh: string }) =>
      revokeResourceGrant(grantId, atLeastAsFresh),
    onSuccess: (grant) => {
      setToken(grant.authorizationToken);
      setRevokeTarget(null);
      toast.success("Access revoked and synchronized.");
      void invalidate();
    },
    onError: () => toast.error("Revocation is pending; affected access remains denied."),
  });

  const submitGrant = (event: FormEvent) => {
    event.preventDefault();
    const organizationWide = subjectType === "Organization";
    if (organizationWide && !confirmedOrganizationWide) {
      setShowOrganizationWarning(true);
      return;
    }
    grantMutation.mutate({
      input: {
        subjectType,
        subjectId: organizationWide ? organizationId : subjectId.trim(),
        relation,
        confirmOrganizationWide: organizationWide && confirmedOrganizationWide,
      },
      atLeastAsFresh: token,
    });
  };

  const changeSubjectType = (value: GrantSubjectType) => {
    setSubjectType(value);
    setConfirmedOrganizationWide(false);
    setShowOrganizationWarning(false);
    if (value === "Organization" && relation === "Manager") setRelation("Editor");
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2"><ShieldCheck className="size-5" /> Sharing</CardTitle>
        <CardDescription>
          Manage direct and organization-wide access. Pending grants are ineffective; pending revocations deny access.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-6">
        <form onSubmit={submitGrant} className="grid gap-4 md:grid-cols-2">
          <div className="space-y-2">
            <Label htmlFor={`${id}-subject-type`}>Share with</Label>
            <select
              id={`${id}-subject-type`}
              aria-label="Share with"
              className="h-9 w-full rounded-md border border-[var(--color-border)] bg-transparent px-3 text-sm"
              value={subjectType}
              onChange={(event) => changeSubjectType(event.target.value as GrantSubjectType)}
            >
              <option value="User">User</option>
              <option value="Organization">All organization members</option>
            </select>
          </div>
          <div className="space-y-2">
            <Label htmlFor={`${id}-relation`}>Access level</Label>
            <select
              id={`${id}-relation`}
              className="h-9 w-full rounded-md border border-[var(--color-border)] bg-transparent px-3 text-sm"
              value={relation}
              onChange={(event) => setRelation(event.target.value as GrantRelation)}
            >
              <option value="Viewer">Viewer — view and download</option>
              <option value="Editor">Editor — viewer plus content changes</option>
              {subjectType === "User" ? <option value="Manager">Manager — editor plus sharing and lifecycle</option> : null}
            </select>
          </div>
          {subjectType === "User" ? (
            <div className="space-y-2 md:col-span-2">
              <Label htmlFor={`${id}-subject-id`}>User ID</Label>
              <Input
                id={`${id}-subject-id`}
                value={subjectId}
                onChange={(event) => setSubjectId(event.target.value)}
                placeholder="Organization member user ID"
                required
              />
            </div>
          ) : (
            <div className="space-y-3 rounded-md border border-[var(--color-border)] p-4 md:col-span-2">
              <p className="flex items-start gap-2 text-sm">
                <UsersRound className="mt-0.5 size-4 shrink-0" />
                Every active organization member will be able to {relation === "Editor" ? "edit" : "view"} this resource.
                This never grants anonymous or cross-organization access.
              </p>
              <label className="flex items-center gap-2 text-sm font-medium">
                <input
                  type="checkbox"
                  checked={confirmedOrganizationWide}
                  onChange={(event) => setConfirmedOrganizationWide(event.target.checked)}
                />
                I understand the organization-wide consequences
              </label>
              {showOrganizationWarning && !confirmedOrganizationWide ? (
                <p role="alert" className="text-sm text-[var(--color-destructive)]">Confirmation is required before sharing.</p>
              ) : null}
            </div>
          )}
          <div className="md:col-span-2">
            <Button type="submit" disabled={grantMutation.isPending || (subjectType === "User" && !subjectId.trim())}>
              {grantMutation.isPending ? "Synchronizing…" : "Grant access"}
            </Button>
          </div>
        </form>

        <div className="space-y-3">
          <h3 className="text-sm font-semibold">Direct and organization grants</h3>
          {grantsQuery.isPending ? <p className="text-sm">Loading grants…</p> : null}
          {grantsQuery.isError ? (
            <p className="text-sm text-[var(--color-muted-foreground)]">Sharing administration requires manager access.</p>
          ) : null}
          {grantsQuery.data?.grants.length === 0 ? <p className="text-sm">No direct or organization-wide grants.</p> : null}
          {grantsQuery.data?.grants.filter((grant) => grant.status !== "Revoked").map((grant) => (
            <div key={grant.id} className="flex flex-wrap items-center justify-between gap-3 rounded-md border border-[var(--color-border)] p-3 text-sm">
              <div>
                <p className="font-medium">{grant.subjectType === "Organization" ? "All organization members" : grant.subjectId}</p>
                <p className="text-[var(--color-muted-foreground)]">{grant.relation} · {grant.status}</p>
              </div>
              <Button type="button" variant="outline" size="sm" onClick={() => setRevokeTarget(grant)}>
                Revoke access
              </Button>
            </div>
          ))}
        </div>

        <div className="space-y-3 border-t border-[var(--color-border)] pt-4">
          <h3 className="text-sm font-semibold">Effective access</h3>
          <p className="text-sm text-[var(--color-muted-foreground)]">Explain direct, inherited, and organization-derived access.</p>
          <Button
            type="button"
            variant="outline"
            disabled={!subjectId.trim() || effectiveQuery.isFetching}
            onClick={() => setEffectiveSubject(subjectId.trim())}
          >
            Explain effective access
          </Button>
          {effectiveQuery.isError ? <p role="alert" className="text-sm">Effective access is unavailable or you are not allowed to inspect this member.</p> : null}
          {effectiveQuery.data ? (
            <div className="space-y-2 text-sm">
              <p className="font-medium">Effective access: {effectiveQuery.data.access}</p>
              {effectiveQuery.data.sources.length === 0 ? <p>No effective grant source.</p> : null}
              {effectiveQuery.data.sources.map((source, index) => (
                <div key={source.grantId ?? `${source.source}-${source.resourceId}-${index}`} className="flex flex-wrap gap-2 rounded-md bg-[var(--color-muted)] p-2">
                  <span className="font-medium">{source.source}</span>
                  <span>{source.relation}</span>
                  <span className="text-[var(--color-muted-foreground)]">{source.resourceType} {source.resourceId}</span>
                </div>
              ))}
            </div>
          ) : null}
        </div>
      </CardContent>

      <Dialog open={revokeTarget !== null} onOpenChange={(open) => { if (!open) setRevokeTarget(null); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Confirm access revocation</DialogTitle>
            <DialogDescription>
              This subject immediately loses access from this grant. Other direct, inherited, or organization grants may still apply.
            </DialogDescription>
          </DialogHeader>
          <DialogBody className="text-sm">
            The revocation is not reported as successful until authorization synchronization confirms it. While pending, affected access remains denied.
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setRevokeTarget(null)}>Cancel</Button>
            <Button
              type="button"
              variant="destructive"
              disabled={revocationMutation.isPending || revokeTarget === null}
              onClick={() => {
                if (revokeTarget) revocationMutation.mutate({ grantId: revokeTarget.id, atLeastAsFresh: token });
              }}
            >
              Confirm revocation
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </Card>
  );
}
