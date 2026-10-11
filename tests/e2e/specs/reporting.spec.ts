import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { apiAs, publishOps, run, signIn } from './helpers';

/**
 * REQ-WS-005, REQ-OPS-004 (ADR 0019, G2-M9c): a builder adds a dashboard and charts a field without the AI, publishes
 * it as one reviewed change, and figures drill like the Overview's; an administrator exports the explorer as CSV while
 * people without the export right never see the button.
 */
test('@REQ-WS-005 a builder creates a dashboard with a new figure and publishes it', async ({ page, request }) => {
  test.setTimeout(120_000);
  await signIn(page, 'morgan@globex.example', 'Globex');
  await page.goto('/dashboards/core.tickets.dashboard');
  await page.getByRole('button', { name: 'Edit dashboard' }).click();
  const editor = page.getByRole('region', { name: 'Edit dashboard' });
  await expect(editor.getByText(/Row 1 · \d of 4 tiles/)).toBeVisible();

  const name = `Priorities ${run}`;
  await editor.getByLabel('New dashboard').fill(name);
  await editor.getByRole('button', { name: 'Create' }).click();
  await page.waitForURL(/\/dashboards\/tenant__dash_priorities_.*edit=1/);
  const view = decodeURIComponent(page.url().split('/dashboards/')[1].split('?')[0]);
  await expect(editor.getByText('No tiles yet. Add the first one below.')).toBeVisible();

  // A new figure over a field, as a bar chart on a new row (metric and tile are one change in the draft).
  await editor.getByLabel('Show as').selectOption('bar');
  await editor.getByLabel('Name').fill('By priority');
  await editor.getByLabel('Group by').selectOption('priority');
  await editor.getByRole('button', { name: 'Add tile' }).click();
  await expect(editor.locator('.chip', { hasText: 'By priority · Bar chart' })).toBeVisible();

  // Publish through the builder's review, then the figure is live and its bars drill into the explorer.
  await editor.getByRole('button', { name: 'Review changes' }).click();
  await page.getByRole('button', { name: 'Review and publish' }).click();
  const dialog = page.getByRole('dialog', { name: 'Publish changes' });
  await dialog.getByRole('button', { name: 'Publish' }).click();
  await expect(page.getByText(/Published revision \d+/).first()).toBeVisible();
  await page.getByRole('button', { name: 'Close the AI builder' }).click();
  await editor.getByRole('button', { name: 'Done' }).click();
  await expect(page.getByRole('heading', { name, level: 1 })).toBeVisible();
  const chart = page.getByRole('region', { name: 'By priority' });
  await expect(chart.getByRole('link').first()).toBeVisible();
  await chart.getByRole('link').first().click();
  await page.waitForURL(/\/records\/core\.tickets\.ticket\?.*filter=/);

  // Clean up so the shared demo workspace keeps one dashboard.
  await publishOps(request, await apiAs(request, 'morgan@globex.example', 'globex'), [{ op: 'view.remove', view }]);
});

test('@REQ-OPS-004 an administrator exports the explorer as CSV and agents do not see Export', async ({ page, browser }) => {
  await signIn(page, 'morgan@globex.example', 'Globex');
  await page.goto('/records/core.tickets.ticket');
  await expect(page.locator('tbody tr').first()).toBeVisible();
  const download = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export' }).click();
  const csv = await readFile((await (await download).path())!, 'utf8');
  expect(csv.split('\n')[0]).toMatch(/^Number,Title,/);
  expect(csv.trim().split('\n').length).toBeGreaterThan(1);

  const agent = await browser.newPage();
  await signIn(agent, 'casey@globex.example', 'Globex');
  await agent.goto('/records/core.tickets.ticket');
  await expect(agent.locator('tbody tr').first()).toBeVisible();
  await expect(agent.getByRole('button', { name: 'Export' })).toHaveCount(0);
  await agent.close();
});
