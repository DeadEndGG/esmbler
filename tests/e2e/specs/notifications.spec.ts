import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';
import { run, signIn } from './helpers';

async function audit(page: Page, name: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  expect(results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${name}: ${v.id}`)).toEqual([]);
}

/**
 * REQ-NOT-001, REQ-NOT-003, REQ-NOT-004, REQ-NOT-005, REQ-NOT-006 (ADR 0021): a ticket becoming P1 reaches its team
 * through the mandatory Tickets route; the inbox links to the record; people mute optional notifications but not
 * mandatory ones; administrators see channels and the delivery history and preview a route without sending anything.
 */
test('@REQ-NOT-004 a P1 ticket reaches the team, mandatory alerts cannot be muted, and deliveries are visible', async ({ page }) => {
  test.setTimeout(120_000);
  await signIn(page, 'morgan@globex.example', 'Globex');

  // Morgan (Service Desk) raises a critical ticket; default assignment gives it to the Service Desk.
  const title = `Core switch down ${run}`;
  await page.goto('/records/core.tickets.ticket/new');
  await page.getByLabel('Title').fill(title);
  await page.getByRole('combobox', { name: /^Priority/ }).selectOption({ label: 'Critical' });
  // A P1 needs its impact; the form asks for it even though the form's layout does not place it.
  const alsoNeeded = page.locator('section.card', { hasText: 'Also needed' });
  await alsoNeeded.getByRole('combobox', { name: /^Impact/ }).selectOption({ index: 1 });
  await page.getByRole('button', { name: /^Create/ }).click();
  await page.waitForURL(/\/records\/core\.tickets\.ticket\/[0-9a-f-]{36}/);
  const ticketUrl = page.url();

  // The inbox shows the P1 alert and links to the ticket. Delivery is asynchronous: the unread filter refetches the
  // inbox in the app (an unread alert shows either way) until it arrives.
  await page.getByRole('link', { name: /^Notifications/ }).click();
  await page.waitForURL(/\/notifications$/);
  const alert = page.getByRole('link', { name: new RegExp(`^P1: .* ${title}$`) });
  await expect(async () => {
    await page.getByLabel('Unread only').click();
    await expect(alert).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 45_000 });
  await audit(page, 'inbox');
  await alert.click();
  await page.waitForURL(ticketUrl);

  // Preferences: the P1 alert is required; email for escalations can be turned off and stays off.
  await page.goto('/notifications');
  await page.getByRole('tab', { name: 'What I receive' }).click();
  const p1 = page.getByRole('row', { name: /P1 alert to the assignee and team/ });
  await expect(p1.getByText('Required')).toBeVisible();
  await expect(p1.getByRole('checkbox', { name: 'Mute all' })).toBeDisabled();
  const escalations = page.getByRole('row', { name: /Escalations to the assignee and team/ });
  const email = escalations.getByRole('checkbox', { name: 'Email' });
  if (!(await email.isChecked())) {
    await email.check();
    await expect(email).toBeChecked();
  }
  await email.uncheck();
  await audit(page, 'preferences');
  await page.goto('/notifications');
  await page.getByRole('tab', { name: 'What I receive' }).click();
  await expect(page.getByRole('row', { name: /Escalations to the assignee and team/ }).getByRole('checkbox', { name: 'Email' })).not.toBeChecked();

  // Administration: deliveries of the alert, and a preview that sends nothing.
  await page.goto('/admin/notifications');
  await expect(page.getByRole('heading', { name: 'Notification channels' })).toBeVisible();
  await audit(page, 'channels');
  await page.getByRole('tab', { name: 'Deliveries' }).click();
  await expect(page.locator('tbody tr', { hasText: title }).filter({ hasText: 'in_app' }).first()).toContainText('sent');
  await audit(page, 'deliveries');
  await page.getByRole('tab', { name: 'Preview a route' }).click();
  await page.getByRole('combobox', { name: 'Route' }).selectOption({ label: 'P1 alert to the assignee and team' });
  await page.getByRole('button', { name: 'Preview', exact: true }).click();
  await expect(page.getByText('This route is mandatory: people cannot mute it.')).toBeVisible();
});
