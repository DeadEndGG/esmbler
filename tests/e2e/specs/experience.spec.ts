import { expect, test } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { openFirstTicket, run, signIn } from './helpers';

/**
 * Experience targets (execution brief section 3), measured on every run:
 * - simple change, prompt to previewed draft: p50 under 10 s;
 * - each AI change visible on the live page within 1 s of its tool call completing: p95.
 * Results are written to test-results/experience.json and summarized in docs/testing/gate-1-test-guide.md.
 */
test('@REQ-LIVE-002 experience targets for live AI editing', async ({ page }) => {
  test.setTimeout(240_000);
  await signIn(page, 'alex@acme.example', 'Acme');
  await openFirstTicket(page);
  await page.getByRole('button', { name: /AI builder/ }).click();
  await expect(page.getByText('Draft preview')).toBeVisible();

  // 20 samples of the most common simple change (add a field to a card), cycling through the page's cards.
  const cards = Array.from({ length: 20 }, (_, i) => ['Details', 'Assignment', 'Classification', 'Requester'][i % 4]);
  const promptToPreview: number[] = [];
  const stepLatency: number[] = [];

  for (const [i, card] of cards.entries()) {
    const label = `Metric ${i} ${run}`;
    // Watch the page (not the chat) for the new field's label and note when it first renders.
    await page.evaluate((text) => {
      const w = window as unknown as { esmblerSeen?: Record<string, number> };
      w.esmblerSeen ??= {};
      const main = document.querySelector('main')!;
      const check = () => {
        if (!w.esmblerSeen![text] && main.innerText.includes(text)) w.esmblerSeen![text] = Date.now();
      };
      new MutationObserver(check).observe(main, { childList: true, subtree: true, characterData: true });
    }, label);

    const box = page.getByLabel('Message to the AI builder');
    await box.fill(`Add a "${label}" field and put it in the ${card} card`);
    const sentAt = await page.evaluate(() => Date.now());
    await box.press('Enter');
    await expect(page.locator('main').getByText(label)).toBeVisible({ timeout: 30_000 });
    await expect(page.getByRole('button', { name: 'Stop' })).toHaveCount(0, { timeout: 30_000 });

    const { seen, events } = await page.evaluate((text) => {
      const w = window as unknown as { esmblerSeen: Record<string, number>; esmblerLiveLog?: { at?: number; receivedAt: number; k: string }[] };
      return { seen: w.esmblerSeen[text], events: w.esmblerLiveLog ?? [] };
    }, label);
    promptToPreview.push(seen - sentAt);
    const applied = events.filter((e) => e.k === 'apply' && typeof e.at === 'number' && e.at <= seen);
    expect(applied.length, 'draft events carry server timestamps').toBeGreaterThan(0);
    stepLatency.push(seen - Math.max(...applied.map((e) => e.at!)));
  }

  const pct = (xs: number[], p: number) => [...xs].sort((a, b) => a - b)[Math.min(xs.length - 1, Math.ceil((p / 100) * xs.length) - 1)];
  const result = {
    measuredAt: new Date().toISOString(),
    samples: cards.length,
    promptToPreviewMs: { p50: pct(promptToPreview, 50), p95: pct(promptToPreview, 95), all: promptToPreview },
    stepVisibleMs: { p50: pct(stepLatency, 50), p95: pct(stepLatency, 95), all: stepLatency },
    targets: { promptToPreviewP50Ms: 10_000, stepVisibleP95Ms: 1_000 },
  };
  mkdirSync('test-results', { recursive: true });
  writeFileSync('test-results/experience.json', JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));

  page.once('dialog', (d) => void d.accept());
  await page.getByRole('button', { name: 'Discard draft' }).click();

  expect(result.promptToPreviewMs.p50).toBeLessThan(10_000);
  expect(result.stepVisibleMs.p95).toBeLessThan(1_000);
});
