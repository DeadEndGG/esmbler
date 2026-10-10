import { expect, test } from '@playwright/test';
import { signIn } from './helpers';

/**
 * REQ-WS-002, REQ-WS-003, REQ-WS-004 (ADR 0019): the Overview dashboard is home; every figure opens the explorer on
 * exactly the records it counts, and coming back from a record returns to the same filtered list.
 */
test('@REQ-WS-003 a dashboard figure drills into the explorer and the record returns to that list', async ({ page }) => {
  await signIn(page, 'casey@globex.example', 'Globex');
  await page.waitForURL(/\/dashboards\/core\.tickets\.dashboard/);
  await expect(page.getByRole('heading', { name: 'Overview', level: 1 })).toBeVisible();

  const tile = page.getByRole('link', { name: /^Open: .+ Open the list$/ });
  await expect(tile).toBeVisible();
  const value = Number((await tile.locator('.value').innerText()).replace(/[^0-9]/g, ''));

  await tile.click();
  await page.waitForURL(/\/records\/core\.tickets\.ticket\?.*filter=/);
  await expect(page.getByRole('status').filter({ hasText: 'Open' })).toBeVisible();
  await expect(page.locator('header p').first()).toContainText(`${value} `);
  const filtered = page.url();

  // Open a record and come back: the drill (and the page position) is kept.
  const first = page.locator('tbody tr a.record-link').first();
  await first.click();
  await page.waitForURL(/\/records\/core\.tickets\.ticket\/[0-9a-f-]{36}/);
  await page.getByRole('navigation', { name: 'Breadcrumb' }).getByRole('link').first().click();
  await expect(page).toHaveURL(filtered);
  await expect(page.getByRole('status').filter({ hasText: 'Open' })).toBeVisible();

  // Grouping keeps the drill and shows a count per group that adds up to the figure.
  await page.getByLabel('Group rows by').selectOption('priority');
  await expect(page.locator('tr.group-row').first()).toBeVisible();
  await expect(page).toHaveURL(/group=priority/);
  const counts = await page.locator('tr.group-row .group-count').allInnerTexts();
  expect(counts.reduce((sum, c) => sum + Number(c), 0)).toBe(value);

  // Clearing the drill shows the saved views again.
  await page.getByRole('button', { name: 'Clear Open' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Open' })).toHaveCount(0);
});

test('@REQ-WS-004 a bar opens the records of its group, and requesters see only their own figures', async ({ page, browser }) => {
  await signIn(page, 'casey@globex.example', 'Globex');
  await page.waitForURL(/\/dashboards\//);
  const chart = page.getByRole('region', { name: 'Workload by team' });
  await expect(chart).toBeVisible();
  const bar = chart.getByRole('link').first();
  const label = (await bar.getAttribute('aria-label'))!;
  const count = Number(label.split(': ').pop()!.replace(/[^0-9]/g, ''));
  await bar.click();
  await page.waitForURL(/filter=/);
  await expect(page.locator('header p').first()).toContainText(`${count} `);

  // The requester's Overview has no team figures (role-gated metrics are removed, not shown empty).
  const requester = await browser.newPage();
  await signIn(requester, 'taylor@globex.example', 'Globex');
  await requester.goto('/dashboards/core.tickets.dashboard');
  await expect(requester.getByRole('link', { name: /^Open: / })).toBeVisible();
  await expect(requester.getByRole('region', { name: 'Workload by team' })).toHaveCount(0);
  await requester.close();
});
