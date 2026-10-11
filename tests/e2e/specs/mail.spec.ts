import AxeBuilder from '@axe-core/playwright';
import { APIRequestContext, Page, expect, test } from '@playwright/test';
import { apiAs, run, signIn } from './helpers';

/** Checks the page in light and dark mode (the theme follows the color scheme through CSS only). */
async function audit(page: Page, name: string): Promise<void> {
  for (const colorScheme of ['light', 'dark'] as const) {
    await page.emulateMedia({ colorScheme });
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
    expect(results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${name} (${colorScheme}): ${v.id}`)).toEqual([]);
  }

  await page.emulateMedia({ colorScheme: null });
}

function message(from: string, subject: string, body: string): string {
  return [`From: ${from}`, 'To: support@globex.example', `Subject: ${subject}`, `Message-ID: <${run}.${Math.random().toString(36).slice(2)}@mail.test>`, 'Content-Type: text/plain; charset=utf-8', '', body, ''].join('\r\n');
}

async function deliver(request: APIRequestContext, token: string, raw: string): Promise<void> {
  const response = await request.post('/api/mail/inbound', { headers: { authorization: `Bearer ${token}`, 'content-type': 'message/rfc822' }, data: raw });
  expect(response.status(), await response.text()).toBe(202);
}

/**
 * REQ-BIZ-003, REQ-ACC-13 (ADR 0023): email to a workspace mailbox opens a ticket for its sender; a message from someone
 * outside the workspace waits for an agent, who accepts it on behalf of a member.
 */
test('@REQ-BIZ-003 email opens a ticket, and an unknown sender waits for an agent to accept it', async ({ page, request }) => {
  test.setTimeout(120_000);
  const headers = await apiAs(request, 'morgan@globex.example', 'globex');
  const saved = await request.put(`/api/admin/mail/inboxes/e2e_${run}`, {
    headers,
    data: { label: `E2E mailbox ${run}`, address: `support-${run}@globex.example`, intake: 'core.tickets.mail.tickets', mode: 'webhook', settings: { senderVerification: 'none' } },
  });
  expect(saved.ok(), await saved.text()).toBeTruthy();
  const token = (await saved.json()).token as string;

  await deliver(request, token, message('taylor@globex.example', `Laptop fan noise ${run}`, 'The fan is loud since the update.'));
  await deliver(request, token, message(`pat-${run}@partner.example`, `Invoice question ${run}`, 'Where is invoice 42?'));

  // Morgan reviews inbound email; processing is asynchronous, so the list is reloaded through the tabs.
  await signIn(page, 'morgan@globex.example', 'Globex');
  await page.getByRole('link', { name: 'Inbound email' }).click();
  await page.waitForURL(/\/mail$/);
  const processed = page.locator('li.card-item', { hasText: `Laptop fan noise ${run}` });
  await expect(async () => {
    await page.getByRole('tab', { name: 'Processed' }).click();
    await expect(processed).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 45_000 });
  await expect(processed).toContainText('opened a ticket');

  const waiting = page.locator('li.card-item', { hasText: `Invoice question ${run}` });
  await expect(async () => {
    await page.getByRole('tab', { name: 'Waiting for review' }).click();
    await expect(waiting).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 45_000 });
  await expect(waiting).toContainText('Sender is not a member of this workspace');
  await waiting.getByRole('button', { name: 'Read' }).click();
  await expect(waiting.locator('pre.message')).toHaveText('Where is invoice 42?');
  await audit(page, 'inbound email');

  await waiting.getByLabel(`Requester for Invoice question ${run}`).selectOption({ label: 'Taylor Brooks' });
  await waiting.getByRole('button', { name: 'Accept' }).click();
  await expect(page.getByText('Accepted: opened a ticket.')).toBeVisible();

  // The ticket from Taylor's own email is hers, from the email channel.
  await page.getByRole('tab', { name: 'Processed' }).click();
  await processed.getByRole('link', { name: 'Open the ticket' }).click();
  await expect(page.getByRole('heading', { name: `Laptop fan noise ${run}` })).toBeVisible();
  await expect(page.locator('main')).toContainText('Taylor Brooks');
});
