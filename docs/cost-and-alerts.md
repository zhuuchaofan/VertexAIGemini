# Spend and error alerts

Nothing here is deployed by the repository: it is set up once in the Google Cloud project. The quota limits in the API
(`Quota` settings) cap each person per day; these alerts watch the project as a whole.

## 1. Budget alert (money)

Needs the billing account id (Billing > Account management; looks like `0123AB-4567CD-89EFAB`) and your project id.
The budget only sends emails. It never stops spending.

```bash
export BILLING_ACCOUNT=0123AB-4567CD-89EFAB
export PROJECT_ID=zhumyproject
```

```bash
gcloud billing budgets create \
  --billing-account="$BILLING_ACCOUNT" \
  --display-name="vertex-ai monthly" \
  --budget-amount=20USD \
  --filter-projects="projects/$PROJECT_ID" \
  --threshold-rule=percent=0.5 \
  --threshold-rule=percent=0.9 \
  --threshold-rule=percent=1.0 \
  --threshold-rule=percent=1.0,basis=forecasted-spend
```

Pick an amount that is a little above a normal month. By default the emails go to the billing account's
administrators. To send them elsewhere, create an email notification channel (below) and add
`--notifications-rule-monitoring-notification-channels=CHANNEL_ID`.

Console route: Billing > Budgets & alerts > Create budget.

## 2. Error alert (the API or web returning 5xx)

Console route (most reliable): Monitoring > Alerting > Create policy.

1. Metric: `Cloud Run Revision > Request count` (`run.googleapis.com/request_count`).
2. Filter: `response_code_class = 5xx`, and `service_name` is `vertex-ai-api` or `vertex-ai-web`.
3. Condition: above `5` over `5 minutes` (adjust to taste).
4. Notification channel: your email.

Command-line route. The flags below could not be run when this was written, so if a command is refused, use the console:

```bash
gcloud monitoring channels create \
  --display-name="me" --type=email --channel-labels=email_address=you@example.com
```

## 3. Watch the daily quota use

`GET /api/admin/quotas/daily` (admin accounts only) lists each person's use for a day, so a surprising bill can be tied
to who and what. People see their own allowance in the sidebar (`GET /api/me/quota`).
