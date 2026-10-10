import AxeBuilder from '@axe-core/playwright';
import { expect, test } from '@playwright/test';
import { openFirstTicket, signIn } from './helpers';

/** REQ-BIZ-005, REQ-ACC-13 (SLA part): the ticket page shows its service levels, measured in service desk hours (ADR 0018). */
test('@REQ-BIZ-005 an agent sees a ticket’s resolution target and goal', async ({ page }) => {
  await signIn(page, 'morgan@globex.example', 'Globex');
  await openFirstTicket(page);

  const panel = page.getByRole('region', { name: 'Service levels' });
  await expect(panel).toBeVisible();
  await expect(panel.getByText('Resolution', { exact: true })).toBeVisible();
  // Seeded tickets are created already picked up, so only resolution runs; first response applies to new tickets.
  await expect(panel.getByText(/Goal .* (in Service desk hours|around the clock)/).first()).toBeVisible();
  await expect(panel.getByRole('meter').first().or(panel.getByText(/^(Met|Missed)/).first())).toBeVisible();

  const results = await new AxeBuilder({ page }).include('[aria-label="Service levels"]').withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  expect(results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => v.id)).toEqual([]);
});
