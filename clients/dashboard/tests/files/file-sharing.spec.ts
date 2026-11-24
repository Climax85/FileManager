import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

const ROOT = {
  id: "019b2a6d-7a4c-7b2d-8e3f-123456789abc",
  organizationId: "root",
  name: "Organization files",
  isRoot: true,
  createdAt: "2026-11-23T12:00:00Z",
  authorizationToken: "root-token",
};

const ACTIVE_GRANT = {
  id: "019b2a6d-7a4c-7b2d-8e3f-123456789abd",
  resourceType: "Folder",
  resourceId: ROOT.id,
  subjectType: "User",
  subjectId: TEST_USER.sub,
  relation: "Viewer",
  status: "Active",
  authorizationToken: "grant-token",
  createdAt: "2026-11-23T12:01:00Z",
  confirmedAt: "2026-11-23T12:01:01Z",
};

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page);
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/authorization/organization/root", ROOT);
});

test("organization-wide sharing explains consequences and requires explicit confirmation", async ({ page }) => {
  await mockJsonResponse(page, `**/api/v1/authorization/resources/Folder/${ROOT.id}/grants`, { grants: [], authorizationToken: "root-token" });
  let posted = false;
  await page.route(`**/api/v1/authorization/resources/Folder/${ROOT.id}/grants`, async (route) => {
    if (route.request().method() !== "POST") return route.continue();
    posted = true;
    const body = route.request().postDataJSON();
    expect(body).toMatchObject({
      subjectType: "Organization",
      subjectId: "root",
      relation: "Viewer",
      confirmOrganizationWide: true,
    });
    expect(body.subjectId).not.toBe("*");
    await route.fulfill({ json: { ...ACTIVE_GRANT, subjectType: "Organization", subjectId: "root" } });
  });

  await page.goto("/file-manager");
  await page.getByLabel("Share with").selectOption("Organization");
  await page.getByRole("button", { name: "Grant access" }).click();
  expect(posted).toBe(false);
  await expect(page.getByText("Every active organization member will be able to view this resource.")).toBeVisible();
  await page.getByLabel("I understand the organization-wide consequences").check();
  await page.getByRole("button", { name: "Grant access" }).click();
  await expect.poll(() => posted).toBe(true);
});

test("dangerous revocation explains immediate access loss before sending DELETE", async ({ page }) => {
  await mockJsonResponse(page, `**/api/v1/authorization/resources/Folder/${ROOT.id}/grants`, {
    grants: [ACTIVE_GRANT],
    authorizationToken: "grant-token",
  });
  let deleted = false;
  await page.route(`**/api/v1/authorization/grants/${ACTIVE_GRANT.id}`, async (route) => {
    deleted = true;
    await route.fulfill({ json: { ...ACTIVE_GRANT, status: "Revoked", authorizationToken: "revoke-token" } });
  });

  await page.goto("/file-manager");
  await page.getByRole("button", { name: "Revoke access" }).click();
  expect(deleted).toBe(false);
  await expect(page.getByText("This subject immediately loses access from this grant.")).toBeVisible();
  await page.getByRole("button", { name: "Confirm revocation" }).click();
  await expect.poll(() => deleted).toBe(true);
});

test("effective access distinguishes direct, inherited, and organization sources", async ({ page }) => {
  await mockJsonResponse(page, `**/api/v1/authorization/resources/Folder/${ROOT.id}/grants`, { grants: [], authorizationToken: "root-token" });
  await mockJsonResponse(
    page,
    `**/api/v1/authorization/resources/Folder/${ROOT.id}/effective-access?subjectId=${TEST_USER.sub}`,
    {
      access: "Editor",
      authorizationToken: "effective-token",
      sources: [
        { grantId: ACTIVE_GRANT.id, source: "Direct", resourceType: "Folder", resourceId: ROOT.id, relation: "Viewer" },
        { grantId: null, source: "Inherited", resourceType: "Folder", resourceId: "parent-id", relation: "Editor" },
        { grantId: null, source: "Organization", resourceType: "Folder", resourceId: ROOT.id, relation: "Viewer" },
      ],
    },
  );

  await page.goto("/file-manager");
  await page.getByLabel("User ID").fill(TEST_USER.sub);
  await page.getByRole("button", { name: "Explain effective access" }).click();
  await expect(page.getByText("Effective access: Editor")).toBeVisible();
  await expect(page.getByText("Direct", { exact: true })).toBeVisible();
  await expect(page.getByText("Inherited", { exact: true })).toBeVisible();
  await expect(page.getByText("Organization", { exact: true })).toBeVisible();
});
