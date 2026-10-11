import { expect, test } from '@playwright/test';
import { ask, run, signIn } from './helpers';

/**
 * REQ-ACC-15 (REQ-WS-001, REQ-WS-002, REQ-WS-003, REQ-WS-006): a new admin creates the whole ticketing workspace from
 * a starter, sees honest preview figures while it is empty, adds a custom field without code, and drills from a
 * dashboard figure into its tickets.
 */
test('@REQ-ACC-15 a new admin creates a ticketing workspace, adds a field and drills from the dashboard', async ({ page }) => {
  test.setTimeout(150_000);
  await signIn(page, 'quinn@globex.example', 'Globex');

  // Create: describe, answer the one question without a default, review the connected pieces, create.
  await page.goto('/workspaces/new');
  await page.getByLabel('Workspace name').fill(`Riverside ${run}`);
  await page.getByLabel(/What do you want to run/).fill('IT ticketing with priorities, an explorer and a dashboard of workload and SLA issues');
  await expect(page.getByText('Matches your description')).toBeVisible();
  await page.getByRole('button', { name: 'Continue' }).click();
  await page.getByLabel('Which team receives new tickets?').fill('Riverside Support');
  await page.getByRole('button', { name: 'Review' }).click();
  await expect(page.getByText(/Overview: Open, Unassigned, SLA breached/).first()).toBeVisible();
  await expect(page.getByText('Riverside Support.', { exact: false })).toBeVisible();
  // Preview as an agent or a requester before anything is created (REQ-WS-001 step 5).
  const personas = page.getByRole('radiogroup', { name: 'Preview as' });
  await expect(personas.getByRole('radio', { name: 'Agent', checked: true })).toBeVisible();
  await expect(page.getByText(/Tickets: sees all tickets, creates them/)).toBeVisible();
  await personas.getByRole('radio', { name: 'Requester' }).click();
  await expect(page.getByText(/Tickets: sees their own tickets, creates them/)).toBeVisible();
  await page.getByRole('button', { name: 'Create workspace' }).click();

  // Empty: guidance and clearly labeled previews, no links into records.
  await page.waitForURL(/\/dashboards\/core\.tickets\.dashboard/, { timeout: 60_000 });
  await expect(page.getByRole('heading', { name: 'No tickets yet' })).toBeVisible();
  await expect(page.locator('esm-kpi-tile .badge', { hasText: 'Preview' }).first()).toBeVisible();
  await expect(page.locator('esm-kpi-tile a')).toHaveCount(0);

  // The first ticket.
  await page.getByRole('link', { name: 'Create the first ticket' }).click();
  await page.waitForURL(/\/records\/core\.tickets\.ticket\/new/);
  await page.getByLabel('Title').fill('Printer on floor 2 is offline');
  await page.getByRole('combobox', { name: /^Priority/ }).selectOption({ label: 'Low' });
  await page.getByRole('button', { name: /^Create/ }).click();
  await page.waitForURL(/\/records\/core\.tickets\.ticket\/[0-9a-f-]{36}/);

  // A custom field through the AI builder, published without code.
  await page.getByRole('button', { name: /AI builder/ }).click();
  await expect(page.getByText('Draft preview')).toBeVisible();
  await ask(page, 'Add a "Vendor" field and put it in the Details card');
  await expect(page.locator('section.card', { hasText: 'Details' }).getByText('Vendor')).toBeVisible();
  await page.getByRole('button', { name: 'Review and publish' }).click();
  const dialog = page.getByRole('dialog', { name: 'Publish changes' });
  await dialog.getByRole('button', { name: 'Publish' }).click();
  await expect(page.getByText(/Published revision \d+/).first()).toBeVisible();
  await page.getByRole('button', { name: 'Close the AI builder' }).click();
  await expect(page.locator('.field', { hasText: 'Vendor' })).toBeVisible();

  // Real figures now, and the Open tile drills into exactly that ticket.
  await page.getByRole('link', { name: 'Overview' }).click();
  await page.waitForURL(/\/dashboards\//);
  await expect(page.getByRole('heading', { name: 'No tickets yet' })).toHaveCount(0);
  const tile = page.getByRole('link', { name: /^Open: 1\. Open the list$/ });
  await tile.click();
  await page.waitForURL(/\/records\/core\.tickets\.ticket\?.*filter=/);
  await expect(page.locator('tbody tr')).toHaveCount(1);
  await expect(page.locator('tbody tr').first()).toContainText('Printer on floor 2 is offline');
});
