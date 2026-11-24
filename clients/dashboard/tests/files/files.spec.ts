// E2E coverage for the /files page (src/pages/files/my-files.tsx): the file
// list, the My files / Shared tab switch, the empty states, and the upload
// dropzone affordance. All API calls are mocked via page.route; the authed
// session is seeded into localStorage and the global shell calls are stubbed
// by installShellMocks. Browser: chromium only, run against the already-
// running Vite dev server.
//
// Gotcha note: the list renders BOTH a hidden mobile card and a desktop row,
// so the same filename matches twice. Row assertions therefore use .last()
// to avoid strict-mode hits. The page fires GET /files/mine on load and GET
// /files/shared only once the Shared tab is selected — mock both.

import { expect, test } from "@playwright/test";
import { mockJsonResponse } from "../helpers/api-mocks";
import { seedAuthedSession, TEST_USER } from "../helpers/auth-seed";
import { installShellMocks } from "../helpers/shell-mocks";

// ─── Fixtures ────────────────────────────────────────────────────────────

const ROOT_ID = "00000000-0000-0000-0000-0000000a1111";

const FILE_REPORT = {
  id: "00000000-0000-0000-0000-0000000f1111",
  ownerType: "MyFiles",
  parentFolderId: ROOT_ID,
  ownerId: null,
  originalFileName: "quarterly-report.pdf",
  contentType: "application/pdf",
  sizeBytes: 248_000,
  visibility: "Private",
  status: "Available",
  scanStatus: 1,
  createdAtUtc: "2026-05-10T10:00:00Z",
  publicUrl: null,
  createdByUserId: TEST_USER.sub,
};

const FILE_SHARED = {
  id: "00000000-0000-0000-0000-0000000f2222",
  ownerType: "MyFiles",
  ownerId: null,
  originalFileName: "team-handbook.pdf",
  contentType: "application/pdf",
  sizeBytes: 512_000,
  visibility: "Public",
  status: "Available",
  scanStatus: 1,
  createdAtUtc: "2026-05-11T10:00:00Z",
  publicUrl: "https://cdn.example.com/team-handbook.pdf",
  createdByUserId: "u-other-1",
};

const FILE_PUBLIC_OWNED_IMAGE = {
  id: "00000000-0000-0000-0000-0000000f3333",
  ownerType: "MyFiles",
  ownerId: null,
  originalFileName: "private-preview.png",
  contentType: "image/png",
  sizeBytes: 128_000,
  visibility: "Public",
  status: "Available",
  scanStatus: 1,
  createdAtUtc: "2026-05-12T10:00:00Z",
  publicUrl: "https://public-bypass.example.test/private-preview.png",
  createdByUserId: TEST_USER.sub,
};

// ─── Shared beforeEach ──────────────────────────────────────────────────

test.beforeEach(async ({ page }) => {
  await seedAuthedSession(page, TEST_USER);
  await installShellMocks(page);
  await mockJsonResponse(page, "**/api/v1/authorization/organization/root**", {
    id: ROOT_ID,
    organizationId: "root",
    name: "Organization files",
    isRoot: true,
    createdAt: "2026-05-01T10:00:00Z",
    authorizationToken: "zed-token",
  });
  // The shared-file "uploaded by" attribution resolves uploader names via
  // GET /identity/users/{id}. Mock it so those rows don't 404-retry.
  await mockJsonResponse(page, "**/api/v1/identity/users/**", {
    id: "u-other-1",
    userName: "bob",
    email: "bob@acme.com",
    firstName: "Bob",
    lastName: "Stone",
  });
});

// ─── List render ──────────────────────────────────────────────────────

test.describe("files — list render", () => {
  test("renders the Files heading and a file row from mocked data", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/files/mine**", [FILE_REPORT]);

    await page.goto("/files");

    await expect(page.getByRole("heading", { name: "Files", level: 1 })).toBeVisible();
    // Both the mobile card and desktop row print the name — scope to the
    // last (desktop table) match so the assertion is deterministic.
    await expect(page.getByText("quarterly-report.pdf").last()).toBeVisible();
    // The "N file(s)" count prints twice (page-header total + list count) —
    // assert the first match so strict mode stays satisfied.
    await expect(page.getByText(/1 file$/).first()).toBeVisible();
  });

  test("shows the upload dropzone affordance by its accessible name", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/files/mine**", [FILE_REPORT]);

    await page.goto("/files");

    // The dropzone is a role="button" carrying its drop-or-browse caption.
    await expect(
      page.getByRole("button", { name: /drop a file or click to browse/i }),
    ).toBeVisible();
  });

  test("shows the empty state when the tenant has no files", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/files/mine**", []);

    await page.goto("/files");

    await expect(page.getByRole("heading", { name: /no files yet/i })).toBeVisible();
    await expect(
      page.getByText(/drop a file above to get started/i),
    ).toBeVisible();
  });
});

// ─── Tab switching ────────────────────────────────────────────────────

test.describe("files — My files / Shared tabs", () => {
  test("switching to Shared loads shared files and renders a shared row", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/files/mine**", [FILE_REPORT]);
    await mockJsonResponse(page, "**/api/v1/files/shared**", [FILE_SHARED]);

    await page.goto("/files");

    // My files row is visible first.
    await expect(page.getByText("quarterly-report.pdf").last()).toBeVisible();

    await page.getByRole("tab", { name: /shared in tenant/i }).click();

    await expect(page.getByText("team-handbook.pdf").last()).toBeVisible();
    // The shared tab is now selected.
    await expect(
      page.getByRole("tab", { name: /shared in tenant/i }),
    ).toHaveAttribute("aria-selected", "true");
  });

  test("Shared tab shows its own empty state when nothing is shared", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/files/mine**", [FILE_REPORT]);
    await mockJsonResponse(page, "**/api/v1/files/shared**", []);

    await page.goto("/files");
    await page.getByRole("tab", { name: /shared in tenant/i }).click();

    await expect(page.getByRole("heading", { name: /nothing shared yet/i })).toBeVisible();
    await expect(
      page.getByText(/direct or inherited resource grants/i),
    ).toBeVisible();
  });
});


test.describe("files — organization upload", () => {
  test("uploads into the root, finalizes, and displays the authorized metadata", async ({ page }) => {
    let finalized = false;
    let requestedParent: string | undefined;
    await page.route("**/api/v1/files/mine**", async (route) => {
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(finalized ? [FILE_REPORT] : []),
      });
    });
    await page.route("**/api/v1/files/upload-url", async (route) => {
      requestedParent = route.request().postDataJSON().parentFolderId;
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({
          fileAssetId: FILE_REPORT.id,
          uploadUrl: "https://minio.example.test/upload/organization-document",
          requiredHeaders: { "Content-Type": "application/pdf" },
          expiresAt: "2026-05-12T10:15:00Z",
        }),
      });
    });
    await page.route("https://minio.example.test/upload/organization-document", async (route) => {
      await route.fulfill({ status: 200, body: "" });
    });
    await page.route(`**/api/v1/files/${FILE_REPORT.id}/finalize`, async (route) => {
      finalized = true;
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(FILE_REPORT),
      });
    });

    await page.goto("/files");
    await page.locator('input[type="file"]').setInputFiles({
      name: "quarterly-report.pdf",
      mimeType: "application/pdf",
      buffer: Buffer.from("%PDF-1.7 test"),
    });

    await expect(page.getByText("quarterly-report.pdf").last()).toBeVisible();
    expect(requestedParent).toBe(ROOT_ID);
    expect(finalized).toBe(true);
  });
});


// ─── Authorized private delivery ───────────────────────────────────────

test.describe("files — authorized delivery", () => {
  test("public visibility never bypasses the signed preview URL", async ({ page }) => {
    const signedUrl = "https://minio.example.test/private-preview.png?X-Amz-Signature=secret";
    let signedUrlRequests = 0;
    await mockJsonResponse(page, "**/api/v1/files/mine**", [FILE_PUBLIC_OWNED_IMAGE]);
    await mockJsonResponse(
      page,
      `**/api/v1/files/${FILE_PUBLIC_OWNED_IMAGE.id}`,
      FILE_PUBLIC_OWNED_IMAGE,
    );
    await page.route(`**/api/v1/files/${FILE_PUBLIC_OWNED_IMAGE.id}/url?inline=true`, async (route) => {
      signedUrlRequests += 1;
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify({ url: signedUrl, expiresAt: "2026-05-12T10:05:00Z" }),
      });
    });
    await page.route(signedUrl, async (route) => {
      await route.fulfill({
        status: 200,
        contentType: "image/png",
        body: Buffer.from(
          "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=",
          "base64",
        ),
      });
    });

    await page.goto("/files");
    await page.getByText("private-preview.png").last().click();

    const preview = page.getByRole("img", { name: "private-preview.png" });
    await expect(preview).toHaveAttribute("src", signedUrl);
    expect(signedUrlRequests).toBeGreaterThan(0);
  });

  test("shows a visible error when authorized delivery is denied", async ({ page }) => {
    await mockJsonResponse(page, "**/api/v1/files/mine**", [FILE_REPORT]);
    await mockJsonResponse(page, `**/api/v1/files/${FILE_REPORT.id}`, FILE_REPORT);
    await mockJsonResponse(
      page,
      `**/api/v1/files/${FILE_REPORT.id}/url?inline=true`,
      { title: "Not Found", detail: "file not found", status: 404 },
      { status: 404 },
    );

    await page.goto("/files");
    await page.getByText("quarterly-report.pdf").last().click();

    await expect(page.getByText(/authorized preview is unavailable/i)).toBeVisible();
    await expect(page.getByRole("button", { name: "Download" })).toHaveCount(0);
  });
});