import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';
import { run, signIn } from './helpers';

async function audit(page: Page, name: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  expect(results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${name}: ${v.id}`)).toEqual([]);
}

/**
 * REQ-AUD-001 (ADR 0022): an administrator searches the audit log, downloads an export, verifies the tamper-evidence
 * chain and places and releases a legal hold.
 */
test('@REQ-AUD-001 an administrator searches, exports and verifies the audit log and manages a legal hold', async ({ page }) => {
  test.setTimeout(90_000);
  await signIn(page, 'morgan@globex.example', 'Globex');
  await page.getByRole('link', { name: 'Audit log' }).click();
  await page.waitForURL(/\/admin\/audit$/);
  await expect(page.getByRole('heading', { name: 'Audit log' })).toBeVisible();

  await page.getByLabel('Operation starts with').fill('auth.');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await expect(page.locator('tbody tr').first()).toContainText('auth.');
  await audit(page, 'audit log');

  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export CSV' }).click();
  expect((await download).suggestedFilename()).toBe('audit.csv');

  await page.getByRole('button', { name: 'Verify now' }).click();
  await expect(page.getByText('The chain is intact.')).toBeVisible();

  const holds = page.getByRole('list', { name: 'Legal holds' });
  await page.getByLabel('Hold reason').fill(`E2E hold ${run}`);
  await page.getByRole('button', { name: 'Place hold' }).click();
  const hold = holds.locator('li', { hasText: `E2E hold ${run}` });
  await expect(hold.getByText('active')).toBeVisible();
  await hold.getByRole('button', { name: 'Release' }).click();
  await expect(hold.getByText('released')).toBeVisible();
});
