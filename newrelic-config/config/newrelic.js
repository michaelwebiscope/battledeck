'use strict'

/**
 * New Relic agent configuration.
 * Deployed to NavalArchive.Web by deploy-newrelic.ps1.
 * API calls go through http://localhost (IIS routes /api to API) so external entity is "localhost".
 */
exports.config = {
  app_name: [process.env.NEW_RELIC_APP_NAME || 'Navalarchive'],
  license_key: process.env.NEW_RELIC_LICENSE_KEY || '',
  distributed_tracing: {
    enabled: true
  },
  span_events: {
    enabled: true,
    max_samples_stored: 10000
  },
  transaction_events: {
    max_samples_stored: 10000
  }
}
