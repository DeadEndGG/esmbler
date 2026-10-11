import AxeBuilder from '@axe-core/playwright';
import { Page, expect, test } from '@playwright/test';
import { apiAs, publishOps, run, signIn } from './helpers';

async function audit(page: Page, name: string): Promise<void> {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa']).analyze();
  expect(results.violations.filter((v) => v.impact === 'serious' || v.impact === 'critical').map((v) => `${name}: ${v.id}`)).toEqual([]);
}

/**
 * REQ-ALR-001 (ADR 0022): a critical escalation raises an alert for the owning team; a member sees it with a badge,
 * acknowledges and assigns it, and the alert resolves itself when the ticket is resolved.
 */
test('@REQ-ALR-001 a critical escalation alerts the team, who acknowledge it, and it resolves with the ticket', async ({ page, request }) => {
  test.setTimeout(120_000);
  const headers = await apiAs(request, 'morgan@globex.example', 'globex');
  const id = `tenant__e2e_alert_${run}`;
  await publishOps(request, headers, [
    { op: 'subscription.add', subscription: { id: `${id}_trigger`, label: `E2E escalated ${run}`, event: 'core.tickets.ticket.escalated', target: 'alert' } },
    { op: 'subscription.add', subscription: { id: `${id}_done`, label: `E2E done ${run}`, event: 'record.transitioned', entity: 'core.tickets.ticket', when: "record.status in ['resolved', 'closed']", target: 'alert' } },
    {
      op: 'alert_rule.add',
      rule: { id, label: `E2E critical ${run}`, trigger: `${id}_trigger`, minSeverity: 'critical', title: `E2E ${run}: {{record.title}}`, owner: { kind: 'group', ref: 'service_desk' }, escalation: [{ afterMinutes: 15 }], resolveOn: [`${id}_done`] },
    },
  ]);

  // A P1 ticket is escalated (through the API: the alert page is what this test is about).
  const created = await request.post('/api/records/core.tickets.ticket', { headers, data: { data: { title: `Data center power ${run}`, priority: 'p1', impact: 'high' } } });
  expect(created.ok(), await created.text()).toBeTruthy();
  let ticket = await created.json();
  for (const [transition, changes] of [['start', {}], ['escalate', { escalation_reason: 'Generator failed' }]] as const) {
    const moved = await request.post(`/api/records/core.tickets.ticket/${ticket.id}/transitions/${transition}`, { headers, data: { revision: ticket.revision, changes } });
    expect(moved.ok(), await moved.text()).toBeTruthy();
    ticket = await moved.json();
  }

  // Morgan (Service Desk) sees the alert; alert processing is asynchronous, so the page is refreshed in the app.
  await signIn(page, 'morgan@globex.example', 'Globex');
  await page.getByRole('link', { name: /^Alerts/ }).click();
  await page.waitForURL(/\/alerts$/);
  const item = page.locator('li.card-item', { hasText: `E2E ${run}: Data center power ${run}` });
  await expect(async () => {
    await page.getByLabel('Only mine').click();
    await expect(item).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 45_000 });
  await expect(item.getByText('open', { exact: true })).toBeVisible();
  await audit(page, 'alerts');

  await item.getByRole('button', { name: 'Acknowledge' }).click();
  await expect(item.getByText('acknowledged', { exact: true })).toBeVisible();
  await item.getByRole('button', { name: 'Assign to me' }).click();
  await expect(item).toContainText('assigned to Morgan Diaz');
  await item.getByRole('button', { name: 'Timeline' }).click();
  await expect(item.getByRole('list', { name: 'Timeline' })).toContainText('Acknowledged by Morgan Diaz');

  // Resolving the ticket resolves the alert.
  const resolved = await request.post(`/api/records/core.tickets.ticket/${ticket.id}/transitions/resolve`, { headers, data: { revision: ticket.revision, changes: { resolution_notes: 'Generator replaced' } } });
  expect(resolved.ok(), await resolved.text()).toBeTruthy();
  await page.getByRole('tab', { name: 'Resolved' }).click();
  await expect(async () => {
    await page.getByRole('tab', { name: 'Active' }).click();
    await page.getByRole('tab', { name: 'Resolved' }).click();
    await expect(page.locator('li.card-item', { hasText: `E2E ${run}: Data center power ${run}` })).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 45_000 });
  await page.getByRole('tab', { name: 'Maintenance windows' }).click();
  await expect(page.getByRole('form', { name: 'New maintenance window' })).toBeVisible();
  await audit(page, 'maintenance windows');
});
