'use strict'

/**
 * New Relic agent configuration.
 * Use API_URL=http://navalarchive-api:5000 so traces show "NavalArchive-API" instead of "localhost:5000"
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
