import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { signIn } from './helpers';

/** REQ-BIZ-004, REQ-ACC-05: service catalog, typed request form, and fulfillment tasks with dependencies (ADR 0015). */
async function audit(page: Page, name: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']).analyze();
  const blocking = results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical');
  expect(blocking.map((v) => `${name}: ${v.id} ${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}`)).toEqual([]);
}

test('@REQ-BIZ-004 a requester orders a laptop and an agent fulfils it task by task', async ({ browser }) => {
  const requesterContext = await browser.newContext();
  const requester = await requesterContext.newPage();
  await signIn(requester, 'taylor@globex.example', 'Globex');

  await requester.getByRole('link', { name: 'Service catalog' }).click();
  await expect(requester.getByRole('heading', { name: 'How can we help?' })).toBeVisible();
  await audit(requester, 'catalog');
  await requester.getByRole('link', { name: /New laptop/ }).click();
  await expect(requester.getByRole('heading', { name: 'New laptop' })).toBeVisible();
  await expect(requester.getByRole('complementary', { name: 'What happens next' }).getByText('Image and enroll the laptop', { exact: true })).toBeVisible();
  await audit(requester, 'request form');

  // Required answers are checked before anything is sent.
  await requester.getByRole('button', { name: /Submit request/ }).click();
  await expect(requester.getByText('Model is required.')).toBeVisible();

  await requester.getByLabel('Model').selectOption({ label: 'MacBook' });
  await requester.getByRole('button', { name: 'Docking station' }).click();
  await requester.getByLabel('Why do you need it?').fill('Video editing for the launch campaign');
  await requester.getByRole('button', { name: /Submit request/ }).click();

  await requester.waitForURL(/\/records\/core\.tickets\.request\/[0-9a-f-]{36}/);
  const url = requester.url();
  await expect(requester.locator('.answer', { hasText: 'Model' })).toContainText('MacBook');
  await expect(requester.locator('.answer', { hasText: 'Accessories' })).toContainText('Docking station');
  await expect(requester.locator('.t-display-badge').first()).toContainText('Submitted');
  await requesterContext.close();

  const agentContext = await browser.newContext();
  const agent = await agentContext.newPage();
  await signIn(agent, 'morgan@globex.example', 'Globex');
  await agent.goto(url);
  await agent.getByRole('button', { name: 'Start fulfillment' }).click();
  await expect(agent.locator('.t-display-badge').first()).toContainText('In fulfillment');

  // Tasks are created by the fulfillment worker; the first one is ready, the rest wait for it.
  const tasks = agent.getByRole('tabpanel');
  await expect(async () => {
    await agent.reload({ waitUntil: 'networkidle' });
    await expect(tasks.locator('tbody tr')).toHaveCount(3, { timeout: 1_000 });
  }).toPass({ timeout: 20_000 });
  await expect(tasks.locator('tbody tr', { hasText: 'Order the laptop' })).toBeVisible();

  await tasks.locator('tbody tr', { hasText: 'Order the laptop' }).getByRole('link').click();
  await expect(agent.locator('.t-display-badge').first()).toContainText('Ready');
  await agent.getByRole('button', { name: 'Complete' }).click();
  await expect(agent.locator('.t-display-badge').first()).toContainText('Done');

  await agent.goto(url);
  await expect(async () => {
    await agent.reload({ waitUntil: 'networkidle' });
    await expect(agent.getByRole('tabpanel').locator('tbody tr', { hasText: 'Image and enroll the laptop' })).toContainText('Ready', { timeout: 1_000 });
  }).toPass({ timeout: 20_000 });
  await audit(agent, 'request record');
  await agentContext.close();
});
