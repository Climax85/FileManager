import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

const ROOT = {
  id: "019b2a6d-7a4c-7b2d-8e3f-123456789abc",
  organizationId: "acme",
  name: "Acme files",
  isRoot: true,
  createdAt: "2026-11-23T12:00:00Z",
  authorizationToken: "zed-token-1",
};

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
});

test("organization root can be retrieved and opened", async ({ page }) => {
  await mockJsonResponse(page, "**/api/v1/authorization/organization/root", ROOT);
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${ROOT.id}`, ROOT);

  await page.goto("/file-manager");
  await expect(page.getByRole("heading", { name: "File Manager" })).toBeVisible();
  await expect(page.getByText("Acme files")).toBeVisible();

  await page.getByRole("link", { name: "Open root folder" }).click();
  await expect(page).toHaveURL(`/file-manager/folders/${ROOT.id}`);
  await expect(page.getByRole("heading", { name: "Acme files" })).toBeVisible();
  await expect(page.getByText("Organization root folder")).toBeVisible();
});

test("denied root access shows a fail-closed empty state", async ({ page }) => {
  await page.route("**/api/v1/authorization/organization/root", async (route) => {
    await route.fulfill({ status: 404, contentType: "application/json", body: JSON.stringify({ title: "Not found" }) });
  });

  await page.goto("/file-manager");

  await expect(page.getByText("No accessible organization root")).toBeVisible();
});
