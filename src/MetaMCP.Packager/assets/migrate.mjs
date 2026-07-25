import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import pg from "pg";
import { drizzle } from "drizzle-orm/node-postgres";
import { migrate } from "drizzle-orm/node-postgres/migrator";

const databaseUrl = process.env.DATABASE_URL;
if (!databaseUrl) {
  console.error("DATABASE_URL is not set.");
  process.exit(2);
}

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const migrationsFolder = join(scriptDirectory, "..", "drizzle");
const pool = new pg.Pool({ connectionString: databaseUrl });

try {
  console.log(`Applying migrations from ${migrationsFolder}`);
  await migrate(drizzle(pool), { migrationsFolder });
  console.log("Database migrations completed.");
} catch (error) {
  console.error("Database migration failed:", error);
  process.exitCode = 1;
} finally {
  await pool.end();
}