import { useId, useState, type FormEvent } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { moveResource } from "@/api/authorization";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

export type MoveResourcePanelProps = {
  resourceType: "Folder" | "File";
  resourceId: string;
  authorizationToken: string;
};

export function MoveResourcePanel({ resourceType, resourceId, authorizationToken }: MoveResourcePanelProps) {
  const id = useId();
  const queryClient = useQueryClient();
  const [destinationFolderId, setDestinationFolderId] = useState("");
  const [confirmed, setConfirmed] = useState(false);
  const [showConfirmationWarning, setShowConfirmationWarning] = useState(false);
  const move = useMutation({
    mutationFn: (destination: string) => moveResource(resourceType, resourceId, destination, authorizationToken),
    onSuccess: async () => {
      toast.success("Resource moved and inherited access updated.");
      await queryClient.invalidateQueries({ queryKey: ["authorization", "folders", resourceId] });
    },
    onError: (error: Error) => toast.error(error.message),
  });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (!confirmed) {
      setShowConfirmationWarning(true);
      return;
    }
    move.mutate(destinationFolderId);
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Move {resourceType.toLowerCase()}</CardTitle>
        <CardDescription>Review how this security-sensitive move changes access.</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={submit} className="space-y-4">
          <div className="rounded-md border border-[var(--color-border)] bg-[var(--color-muted)] p-3 text-sm">
            <p>Direct grants stay unchanged.</p>
            <p>{resourceType === "Folder"
              ? "Inherited access for this folder and its entire subtree will follow the destination folder."
              : "Inherited access for this file will follow the destination folder."}</p>
            <p>People can gain or lose inherited access immediately after synchronization is confirmed.</p>
          </div>
          <div className="space-y-2">
            <Label htmlFor={`${id}-destination`}>Destination folder ID</Label>
            <Input
              id={`${id}-destination`}
              value={destinationFolderId}
              onChange={(event) => setDestinationFolderId(event.target.value)}
              required
            />
          </div>
          <div className="flex items-start gap-2">
            <input
              id={`${id}-confirmed`}
              type="checkbox"
              checked={confirmed}
              onChange={(event) => {
                setConfirmed(event.target.checked);
                setShowConfirmationWarning(false);
              }}
            />
            <Label htmlFor={`${id}-confirmed`}>I understand the move access consequences</Label>
          </div>
          {showConfirmationWarning ? (
            <p role="alert" className="text-sm text-[var(--color-destructive)]">
              Confirm the access consequences before moving this resource.
            </p>
          ) : null}
          <Button type="submit" disabled={move.isPending || destinationFolderId.trim().length === 0}>
            {move.isPending ? "Moving…" : `Move ${resourceType.toLowerCase()}`}
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}
