import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

const FOLDER = {
  id: "019b2a6d-7a4c-7b2d-8e3f-123456789abc",
  organizationId: "root",
  name: "Projects",
  isRoot: false,
  parentId: "019b2a6d-7a4c-7b2d-8e3f-123456789abe",
  createdAt: "2026-11-23T12:00:00Z",
  authorizationToken: "folder-token",
  canEdit: true,
};

const DESTINATION = "019b2a6d-7a4c-7b2d-8e3f-123456789abd";

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${FOLDER.id}`, FOLDER);
});

test("move explains inherited access consequences and requires explicit confirmation", async ({ page }) => {
  let posted = false;
  await page.route(`**/api/v1/authorization/resources/Folder/${FOLDER.id}/move`, async (route) => {
    const body = route.request().postDataJSON();
    expect(body.destinationFolderId).toBe(DESTINATION);
    expect(route.request().headers()["x-authz-token"]).toBe("folder-token");
    posted = true;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        resourceType: "Folder",
        resourceId: FOLDER.id,
        parentFolderId: DESTINATION,
        operationId: "019b2a6d-7a4c-7b2d-8e3f-123456789abf",
        synchronizationStatus: "Confirmed",
        authorizationToken: "move-token",
      }),
    });
  });

  await page.goto(`/file-manager/folders/${FOLDER.id}`);
  await expect(page.getByText("Direct grants stay unchanged.")).toBeVisible();
  await expect(page.getByText("Inherited access for this folder and its entire subtree will follow the destination folder.")).toBeVisible();
  await page.getByLabel("Destination folder ID").fill(DESTINATION);
  await page.getByRole("button", { name: "Move folder" }).click();
  expect(posted).toBe(false);
  await expect(page.getByText("Confirm the access consequences before moving this resource.")).toBeVisible();
  await page.getByLabel("I understand the move access consequences").check();
  await page.getByRole("button", { name: "Move folder" }).click();
  await expect.poll(() => posted).toBe(true);
});
