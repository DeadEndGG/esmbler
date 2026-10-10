import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { openFirstTicket, signIn, openTickets } from './helpers';

/** REQ-DS-003: WCAG 2.2 AA checks on the main screens, including draft mode. Serious and critical issues fail. */
async function audit(page: Page, name: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
  const blocking = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(blocking.map((v) => `${name}: ${v.id} (${v.impact}) ${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([]);
}

test('@REQ-DS-003 sign-in, list, record and draft pages have no serious accessibility violations', async ({ page }) => {
  await page.goto('/login');
  await audit(page, 'login');

  await signIn(page, 'alex@acme.example', 'Acme');
  await openTickets(page);
  await audit(page, 'list');

  await openFirstTicket(page);
  await expect(page.locator('section.card').first()).toBeVisible();
  await audit(page, 'record');

  await page.getByRole('button', { name: /AI builder/ }).click();
  await expect(page.getByText('Draft preview')).toBeVisible();
  await audit(page, 'record in draft mode');

  await page.getByRole('button', { name: 'Close the AI builder' }).click();
  await page.goto('/admin/configuration');
  await expect(page.getByRole('heading', { name: 'Revisions' })).toBeVisible();
  await audit(page, 'configuration');
});

test.describe('dark mode', () => {
  test.use({ colorScheme: 'dark' });

  test('@REQ-DS-003 sign-in, list, record and draft pages have no serious accessibility violations in dark mode', async ({ page }) => {
    await page.goto('/login');
    await audit(page, 'login (dark)');

    await signIn(page, 'alex@acme.example', 'Acme');
    await openTickets(page);
    await page.keyboard.press('j');
    await audit(page, 'list (dark)');

    await openFirstTicket(page);
    await expect(page.locator('section.card').first()).toBeVisible();
    await audit(page, 'record (dark)');

    await page.getByRole('button', { name: /AI builder/ }).click();
    await expect(page.getByText('Draft preview')).toBeVisible();
    await audit(page, 'record in draft mode (dark)');
    await page.getByRole('button', { name: 'Close the AI builder' }).click();
  });
});
