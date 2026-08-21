// Cost alerting for one resource group.
//
// Deployed AT RESOURCE-GROUP SCOPE on purpose. A budget created at subscription scope tracks
// everything in the subscription, and this subscription already contains unrelated resources —
// a Cognitive Services account used by Capture/CapturePreBuilt. A subscription budget would
// alert on that spend as though it were ours, which is worse than no alert: it trains people
// to ignore the email.
//
// A resource-group-scoped budget counts only costs attributed to that resource group. The
// explicit ResourceGroupName dimension filter below is belt and braces — it makes the intent
// legible in the template and in the portal, rather than something you have to know about
// ARM scoping rules to infer.
//
// AN AZURE BUDGET IS AN ALERT, NOT A CAP. Reaching the amount sends email. It does not stop
// billing, throttle anything, or deallocate resources. The only hard control is deleting the
// resource group.

targetScope = 'resourceGroup'

@description('Budget name. Must be unique within the resource group.')
param budgetName string

@description('Monthly amount in the billing currency. An alert threshold, not a cap.')
@minValue(1)
param monthlyAmount int

@description('Who gets the alert email. An empty array disables the budget entirely.')
param contactEmails array

@description('First of the month the budget starts, yyyy-MM-01. Azure REJECTS a change to this on an existing budget, so the caller reads the existing value back rather than recomputing it.')
param startDate string

@description('Last day the budget is evaluated, yyyy-MM-01.')
param endDate string

resource budget 'Microsoft.Consumption/budgets@2023-05-01' = if (!empty(contactEmails)) {
  name: budgetName
  properties: {
    category: 'Cost'
    amount: monthlyAmount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: startDate
      endDate: endDate
    }
    filter: {
      dimensions: {
        name: 'ResourceGroupName'
        operator: 'In'
        values: [
          resourceGroup().name
        ]
      }
    }
    notifications: {
      // 80% actual: something is running that you may not have meant to leave running.
      actualEighty: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 80
        contactEmails: contactEmails
        thresholdType: 'Actual'
      }
      // 100% forecast: the month is on track to exceed. Arrives while there is still time
      // to do something, which is the only kind of cost alert that changes an outcome.
      forecastHundred: {
        enabled: true
        operator: 'GreaterThan'
        threshold: 100
        contactEmails: contactEmails
        thresholdType: 'Forecasted'
      }
    }
  }
}

output budgetName string = budgetName
output budgetEnabled bool = !empty(contactEmails)
