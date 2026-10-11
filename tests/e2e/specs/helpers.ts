import { APIRequestContext, Page, expect } from '@playwright/test';

export const PASSWORD = 'Esmbler!2026';

/** Signs in through the login page and opens the given workspace's production environment. */
export async function signIn(page: Page, email: string, workspace: string): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL(/\/(workspaces|records|dashboards|catalog)/);
  if (page.url().includes('/workspaces')) {
    await page.locator('li', { hasText: workspace }).getByRole('button').first().click();
  }
  // Home is the first dashboard the person can see (ADR 0019), otherwise their first list.
  await page.waitForURL(/\/(records|dashboards|catalog)\b/);
}

/** Opens the ticket explorer. */
export async function openTickets(page: Page): Promise<void> {
  await page.goto('/records/core.tickets.ticket');
  await expect(page.locator('tbody tr').first()).toBeVisible();
}

/** A short suffix so labels are unique when the suite runs again against the same stack. */
export const run = Date.now().toString(36).slice(-5);

export async function openFirstTicket(page: Page): Promise<void> {
  await page.goto('/records/core.tickets.ticket');
  const first = page.locator('tbody tr a').first();
  await expect(first).toBeVisible();
  await first.click();
  await page.waitForURL(/\/records\/core\.tickets\.ticket\/[0-9a-f-]{36}/);
}

/** Sends a message to the AI builder and waits until it finishes. */
export async function ask(page: Page, text: string): Promise<void> {
  const box = page.getByLabel('Message to the AI builder');
  await box.fill(text);
  await box.press('Enter');
  await expect(page.locator('.msg.assistant').last()).toBeVisible({ timeout: 45_000 });
  await expect(page.getByRole('button', { name: 'Stop' })).toHaveCount(0, { timeout: 45_000 });
}

/** Signs in through the API (no UI) and returns headers for calls into the given workspace's production environment. */
export async function apiAs(request: APIRequestContext, email: string, workspace: string): Promise<Record<string, string>> {
  const login = await request.post('/api/auth/login', { data: { email, password: PASSWORD } });
  expect(login.ok()).toBeTruthy();
  const first = (await login.json()).accessToken as string;
  const switched = await request.post('/api/auth/switch', { data: { tenant: workspace }, headers: { Authorization: `Bearer ${first}` } });
  expect(switched.ok()).toBeTruthy();
  return { Authorization: `Bearer ${(await switched.json()).accessToken}` };
}

/** Applies overlay ops as one draft change and publishes it (the signed-in person needs configuration rights). */
export async function publishOps(request: APIRequestContext, headers: Record<string, string>, ops: object[]): Promise<void> {
  const opened = await request.post('/api/drafts', { headers });
  expect(opened.ok()).toBeTruthy();
  let version = (await opened.json()).version as number;
  for (const op of ops) {
    const applied = await request.post('/api/drafts/current/commands', { headers, data: { op, expectedVersion: version } });
    expect(applied.ok(), await applied.text()).toBeTruthy();
    version = (await applied.json()).version;
  }
  const draft = await (await request.get('/api/drafts/current', { headers })).json();
  const published = await request.post('/api/config/publish', { headers, data: { artifactHash: draft.artifactHash, expectedRevision: draft.publishedRevision } });
  expect(published.ok(), await published.text()).toBeTruthy();
}
