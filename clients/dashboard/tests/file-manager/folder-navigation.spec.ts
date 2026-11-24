import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

const ROOT = {
  id: "019b2a6d-7a4c-7b2d-8e3f-123456789abc",
  organizationId: "acme",
  name: "Acme files",
  isRoot: true,
  parentId: null,
  createdAt: "2026-11-23T12:00:00Z",
  authorizationToken: "zed-root",
  canEdit: true,
};
const PROJECTS = {
  ...ROOT,
  id: "019b2a6d-7a4c-7b2d-8e3f-123456789abd",
  name: "Projects",
  isRoot: false,
  parentId: ROOT.id,
  authorizationToken: "zed-projects",
};
const YEAR = {
  ...PROJECTS,
  id: "019b2a6d-7a4c-7b2d-8e3f-123456789abe",
  name: "2026",
  parentId: PROJECTS.id,
  authorizationToken: "zed-year",
};

function childPage(items: typeof PROJECTS[], nextCursor: string | null = null) {
  return { items, nextCursor, authorizationToken: "zed-page" };
}

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
});

test("editor navigates nested folders and creates a child at the current depth", async ({ page }) => {
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${ROOT.id}`, ROOT);
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${PROJECTS.id}`, PROJECTS);
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${YEAR.id}`, YEAR);
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${ROOT.id}/children*`, childPage([PROJECTS]));
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${PROJECTS.id}/children*`, childPage([YEAR]));
  let created = false;
  await page.route(`**/api/v1/authorization/folders/${YEAR.id}/children*`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(childPage(created ? [{ ...YEAR, id: "019b2a6d-7a4c-7b2d-8e3f-123456789abf", name: "Approved", parentId: YEAR.id }] : [])),
    });
  });
  await page.route("**/api/v1/authorization/folders", async (route) => {
    if (route.request().method() !== "POST") return route.fallback();
    expect(route.request().postDataJSON()).toMatchObject({ parentId: YEAR.id, name: "Approved" });
    created = true;
    await route.fulfill({
      status: 201,
      contentType: "application/json",
      body: JSON.stringify({ ...YEAR, id: "019b2a6d-7a4c-7b2d-8e3f-123456789abf", name: "Approved", parentId: YEAR.id }),
    });
  });

  await page.goto(`/file-manager/folders/${ROOT.id}`);
  await page.getByRole("link", { name: "Projects" }).click();
  await expect(page).toHaveURL(`/file-manager/folders/${PROJECTS.id}`);
  await page.getByRole("link", { name: "2026" }).click();
  await expect(page.getByRole("heading", { name: "2026" })).toBeVisible();
  await page.getByLabel("New folder name").fill("Approved");
  await page.getByRole("button", { name: "Create folder" }).click();

  await expect(page.getByRole("link", { name: "Approved" })).toBeVisible();
});

test("viewer can navigate but is not offered create or rename controls", async ({ page }) => {
  const viewerRoot = { ...ROOT, canEdit: false };
  const viewerProjects = { ...PROJECTS, canEdit: false };
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${ROOT.id}`, viewerRoot);
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${PROJECTS.id}`, viewerProjects);
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${ROOT.id}/children*`, childPage([viewerProjects]));
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${PROJECTS.id}/children*`, childPage([]));

  await page.goto(`/file-manager/folders/${ROOT.id}`);
  await expect(page.getByRole("button", { name: "Create folder" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Rename folder" })).toHaveCount(0);
  await page.getByRole("link", { name: "Projects" }).click();

  await expect(page.getByRole("heading", { name: "Projects" })).toBeVisible();
  await expect(page.getByText("No child folders.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Create folder" })).toHaveCount(0);
});

test("folder cursor loads the next authorized page without displaying an exact total", async ({ page }) => {
  await mockJsonResponse(page, `**/api/v1/authorization/folders/${ROOT.id}`, ROOT);
  await page.route(`**/api/v1/authorization/folders/${ROOT.id}/children*`, async (route) => {
    const url = new URL(route.request().url());
    const second = url.searchParams.get("cursor") === "second-page";
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(second
        ? childPage([{ ...YEAR, name: "Second page folder", parentId: ROOT.id }])
        : childPage([PROJECTS], "second-page")),
    });
  });

  await page.goto(`/file-manager/folders/${ROOT.id}`);
  await expect(page.getByRole("link", { name: "Projects" })).toBeVisible();
  await expect(page.getByText(/total/i)).toHaveCount(0);
  await page.getByRole("button", { name: "Load more folders" }).click();

  await expect(page.getByRole("link", { name: "Second page folder" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Load more folders" })).toHaveCount(0);
});

test("denied folder navigation shows a fail-closed state", async ({ page }) => {
  await page.route(`**/api/v1/authorization/folders/${ROOT.id}`, async (route) => {
    await route.fulfill({ status: 404, contentType: "application/json", body: JSON.stringify({ title: "Not found" }) });
  });

  await page.goto(`/file-manager/folders/${ROOT.id}`);

  await expect(page.getByText("Folder unavailable")).toBeVisible();
  await expect(page.getByText("The folder does not exist or you do not have access.")).toBeVisible();
});
