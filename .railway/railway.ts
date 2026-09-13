import { defineRailway, postgres, project, service } from "railway/iac";

/**
 * Modbot's Railway project: one service plus one Postgres (foundation spec 2.5).
 *
 * This file supersedes `railway.json`. Railway deprecated Config as Code — new services cannot
 * opt into it, and existing ones stop honouring it on 2026-12-01 — so `railway.json` is kept only
 * for deployments created before that cutoff. Everything below is the supported path, and it can
 * do the one thing the JSON file cannot: declare the database alongside the service, so a fresh
 * install is a single apply rather than "create a Postgres, then find its URL, then paste it".
 */
export default defineRailway(() => {
  const db = postgres("postgres");

  const modbot = service("modbot", {
    // Readiness, not liveness. The deploy should not be marked healthy until the new container has
    // migrated the schema and can reach the database -- which is exactly what /health/ready proves
    // and /health/live deliberately does not.
    healthcheck: "/health/ready",

    // Generous, because the healthcheck is racing a migration on a database that may have just
    // been provisioned. A timeout here rolls back a deployment that was only slow.
    healthcheckTimeout: 300,

    // One. Modbot is a single-tenant appliance holding one VRChat session behind an in-process
    // IVRChatGate, and section 4.3's rate limit is opaque and punitive: a second replica would
    // double outbound traffic against a budget neither instance can see. The seam for a
    // Redis-lease gate exists (2.5); until it is exercised, this stays at one.
    replicas: 1,

    env: {
      // The only variable an operator must not set by hand. PORT is injected by Railway, and
      // SEQ_URL is optional -- see ModbotEnvironment. Everything else Modbot needs lives in the
      // database and is entered through the onboarding wizard (spec 2.6).
      DATABASE_URL: db.env.DATABASE_URL,
    },
  });

  return project("modbot", {
    resources: [db, modbot],
  });
});
