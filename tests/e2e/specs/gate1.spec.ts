import { expect, test } from '@playwright/test';
import { ask, openFirstTicket, run, signIn } from './helpers';

const vendor = `Vendor ${run}`;

test.describe.configure({ mode: 'serial' });

test('@REQ-ACC-01 @REQ-LIVE-002 tenant customizes Tickets with the AI builder, sees it live, publishes, and operates records', async ({ page }) => {
  await signIn(page, 'alex@acme.example', 'Acme');
  await expect(page.locator('tbody tr').first()).toBeVisible();

  await openFirstTicket(page);
  await page.getByRole('button', { name: /AI builder/ }).click();
  await expect(page.getByRole('complementary', { name: 'AI builder' })).toBeVisible();
  await expect(page.getByText('Draft preview')).toBeVisible();

  await ask(page, `Add a "${vendor}" field and put it in the Details card`);
  // Live: the field shows up on the page in draft mode, marked as new, before anything is published.
  const details = page.locator('section.card', { hasText: 'Details' });
  await expect(details.getByText(vendor)).toBeVisible();
  await expect(page.locator('.chip', { hasText: 'New' }).first()).toBeVisible();
  await expect(page.locator('.steps li', { hasText: vendor })).toBeVisible();

  await page.getByRole('button', { name: 'Review and publish' }).click();
  const dialog = page.getByRole('dialog', { name: 'Publish changes' });
  await expect(dialog.getByText('The draft compiles and passes validation.')).toBeVisible();
  await dialog.getByRole('button', { name: 'Publish' }).click();
  await expect(page.getByText(/Published revision \d+/).first()).toBeVisible();

  await page.getByRole('button', { name: 'Close the AI builder' }).click();
  await expect(page.getByText('Draft preview')).toHaveCount(0);
  const field = page.locator('.field', { hasText: vendor });
  await expect(field).toBeVisible();

  // Operate: edit the new field inline; the value survives a reload.
  await field.getByRole('button', { name: `Edit ${vendor}` }).click();
  const input = field.locator('input');
  await input.fill('Workday');
  const saved = page.waitForResponse((r) => r.request().method() === 'PATCH' && r.url().includes('/api/records/'));
  await input.press('Enter');
  const response = await saved;
  expect(response.status(), await response.text()).toBe(200);
  await expect(field.getByText('Workday')).toBeVisible();
  await page.reload();
  await expect(page.locator('.field', { hasText: vendor }).getByText('Workday')).toBeVisible();
});

test('@REQ-ACC-01 the other tenant does not see the customization', async ({ page }) => {
  await signIn(page, 'morgan@globex.example', 'Globex');
  await openFirstTicket(page);
  await expect(page.locator('section.card', { hasText: 'Details' })).toBeVisible();
  await expect(page.getByText(vendor)).toHaveCount(0);
});

test('@REQ-ACC-09 draft changes are invisible to people who are not building', async ({ browser }) => {
  const builder = await browser.newPage();
  await signIn(builder, 'alex@acme.example', 'Acme');
  await openFirstTicket(builder);
  await builder.getByRole('button', { name: /AI builder/ }).click();
  const draftOnly = `Draft only ${run}`;
  await ask(builder, `Add a "${draftOnly}" field and put it in the Details card`);
  await expect(builder.getByText(draftOnly).first()).toBeVisible();

  const agent = await browser.newPage();
  await signIn(agent, 'sam@acme.example', 'Acme');
  await openFirstTicket(agent);
  await expect(agent.locator('section.card', { hasText: 'Details' })).toBeVisible();
  await expect(agent.getByText(draftOnly)).toHaveCount(0);

  // Clean up: discard the draft; the published page is unchanged for the builder too.
  builder.once('dialog', (d) => void d.accept());
  await builder.getByRole('button', { name: 'Discard draft' }).click();
  await expect(builder.getByText(/Draft discarded/)).toBeVisible();
  await builder.close();
  await agent.close();
});

test('@REQ-ACC-02 a stale inline edit is refused and the page shows the latest version', async ({ browser }) => {
  const first = await browser.newPage();
  const second = await browser.newPage();
  await signIn(first, 'sam@acme.example', 'Acme');
  await signIn(second, 'alex@acme.example', 'Acme');
  await openFirstTicket(first);
  await second.goto(first.url());

  const email = (p: typeof first) => p.locator('.field', { hasText: 'Contact email' }).first();
  const sams = `sam.${run}@example.com`;
  await email(first).getByRole('button', { name: 'Edit Contact email' }).click();
  await email(first).locator('input').fill(sams);
  const saved = first.waitForResponse((r) => r.request().method() === 'PATCH');
  await email(first).locator('input').press('Enter');
  expect((await saved).status()).toBe(200);

  // Alex still has the page as it was before Sam's save.
  await email(second).getByRole('button', { name: 'Edit Contact email' }).click();
  await email(second).locator('input').fill(`alex.${run}@example.com`);
  const refused = second.waitForResponse((r) => r.request().method() === 'PATCH');
  await email(second).locator('input').press('Enter');
  expect((await refused).status()).toBe(409);
  await expect(second.getByText(/changed since you opened it/)).toBeVisible();
  await expect(email(second).getByText(sams)).toBeVisible();
  await first.close();
  await second.close();
});

test('@REQ-ACC-03 the module upgrade keeps the customization', async ({ page }) => {
  await signIn(page, 'alex@acme.example', 'Acme');
  await page.goto('/admin/configuration');
  const modules = page.locator('section', { has: page.getByRole('heading', { name: 'Modules' }) });
  const row = modules.locator('tbody tr', { hasText: 'core.tickets' });
  await expect(row).toBeVisible();
  const preview = row.getByRole('button', { name: 'Preview upgrade' });
  if (await preview.count()) {
    await preview.click();
    await expect(page.getByText('Your customizations carry over.')).toBeVisible();
    await page.getByRole('button', { name: 'Upgrade', exact: true }).click();
    await expect(page.getByText(/upgraded to 1\.1\.0/)).toBeVisible({ timeout: 60_000 });
  }
  await expect(row.getByText('Up to date')).toBeVisible();
  await openFirstTicket(page);
  await expect(page.locator('.field', { hasText: vendor })).toBeVisible();
  await expect(page.locator('.field', { hasText: 'Impact' }).first()).toBeVisible();
});
