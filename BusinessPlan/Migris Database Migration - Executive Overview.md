---
pdf_options:
  format: Letter
  margin: 30mm 25mm
  headerTemplate: '<div style="font-size:8px; width:100%; text-align:right; padding-right:25mm; color:#666;">Migris Technology — Confidential</div>'
  footerTemplate: '<div style="font-size:8px; width:100%; text-align:center; color:#666;"><span class="pageNumber"></span> / <span class="totalPages"></span></div>'
  displayHeaderFooter: true
stylesheet: []
---

<div style="text-align:center; margin-top:80px; margin-bottom:60px;">

# Migris Database Migration Platform

## Executive Overview

### SQL Server to PostgreSQL — Done Right

<br/><br/>

*A smarter, safer path from Microsoft SQL Server to PostgreSQL that keeps your applications running throughout the process.*

<br/><br/><br/>

**Migris Technology**

</div>

<div style="page-break-after: always;"></div>

## The Problem with Traditional Database Migration

Organizations migrating from Microsoft SQL Server to PostgreSQL face a harsh reality: most migration tools focus narrowly on converting database schema and moving data. They treat the database as an isolated artifact, ignoring the applications that depend on it.

The result is predictable. Schema gets converted. Data gets moved. And then applications break — often in subtle, hard-to-diagnose ways. Stored procedures behave differently. Query patterns that worked in SQL Server return wrong results or fail entirely in PostgreSQL. Teams spend months debugging issues that surface only under real workload conditions.

**The industry failure rate for large-scale database migrations exceeds 50%.** Not because the technology is impossible, but because the approach is wrong.

---

## How Migris Is Different

The Migris Platform increases the probability of a successful migration at every stage of the process — not just during the conversion step. We do this through four core differentiators:

### 1. Pre-Migration Preparation

Before a single object is converted, Migris assesses and prepares your source database for migration. Our **Migration Assessment** tool connects to your live SQL Server, analyzes actual workload patterns, and produces a readiness score with detailed risk analysis.

This step identifies blocking features, quantifies effort by category, and recommends whether to proceed, redesign specific components, or reconsider the migration entirely. Issues are resolved *before* the conversion begins — not discovered after the fact in production.

**Why this matters:** Other tools start converting immediately and leave you to discover incompatibilities downstream. Migris eliminates surprises early, when they are cheapest to address.

### 2. Application-Aware Conversion Decisions

Standard migration tools apply generic conversion rules: map data types, rename functions, hope for the best. Migris makes conversion decisions that align with keeping **your applications working** — not just producing syntactically valid PostgreSQL.

Our AI-assisted conversion engine understands the *intent* behind stored procedures, triggers, and complex views. It doesn't just translate syntax; it ensures the converted objects will behave the same way your applications expect them to. When confidence is low, objects are flagged for human review rather than silently producing incorrect output.

A visual Conversion Reviewer lets your team inspect every decision side-by-side — original T-SQL on the left, generated PostgreSQL on the right — and make corrections before anything touches the target database.

**Why this matters:** A syntactically correct migration that breaks application behavior is a failed migration. Migris optimizes for functional equivalence, not just structural translation.

### 3. AI-Accelerated Conversion with Human-Focused Attention

Migris leverages artificial intelligence (Amazon Bedrock) to convert complex database objects — stored procedures, functions, triggers, and sophisticated views — that rule-based tools cannot handle reliably.

But AI isn't applied blindly. The platform:

- **Routes intelligently** — Simple, well-defined objects (tables, indexes, constraints) go through deterministic rule-based conversion. Complex objects go through AI. Each object gets the right treatment.
- **Scores confidence** — Every AI conversion receives a confidence rating. Low-confidence results are flagged for expert human review.
- **Audits everything** — Full traceability from source object to conversion decision to output, including the exact AI interaction that produced each result.

**Why this matters:** AI makes the migration dramatically faster by handling hundreds of complex objects that would take weeks of manual effort. But it also tells you exactly where the human experts need to focus their time — on the 5-10% of objects that genuinely require human judgment.

### 4. PgPassthrough — Test Before You're Done

This is perhaps the most significant differentiator in the Migris platform.

**PgPassthrough** is a real-time protocol proxy that lets your existing applications connect to PostgreSQL using their current SQL Server ODBC drivers — without any application code changes. It intercepts T-SQL commands over the wire and converts them to PostgreSQL commands on the fly.

This means your application can run in a testing environment against the migrated PostgreSQL database *before all conversion work is complete*. PgPassthrough handles the queries that haven't been natively ported yet, while tracking exactly which commands still need direct conversion.

The benefits are transformational:

- **Immediate validation** — Run your full application test suite against the migrated database on day one of conversion, not after months of work.
- **Progressive migration** — As objects are converted natively, PgPassthrough handles less and less. You can see migration progress as a declining percentage of proxied commands.
- **Risk elimination** — Discover integration issues weeks or months earlier than with traditional approaches, when fixing them is straightforward.
- **Business continuity** — Stakeholders can see the application working throughout the migration process, not just at the end after all investment has been made.

**Why this matters:** Traditional migrations are a leap of faith. You invest months of effort and budget before discovering whether the result actually works. PgPassthrough turns migration into a progressive, testable, observable process from beginning to end.

---

## The Migris Migration Pipeline

<br/>

| Phase | What Happens | Key Outcome |
|-------|-------------|-------------|
| **1. Assessment** | Analyze live workloads and score migration readiness | Know your risk before you start |
| **2. Schema Conversion** | AI + rules convert all database objects | Functionally equivalent PostgreSQL schema |
| **3. Human Review** | Visual side-by-side review of complex conversions | Expert validation where it matters |
| **4. Mapping Generation** | Generate runtime translation mappings for PgPassthrough | Bridge between old and new |
| **5. Data Migration** | Move data in dependency order with integrity checks | Complete data transfer with verification |
| **6. PgPassthrough Deployment** | Applications connect and run against PostgreSQL immediately | Validation without waiting for full conversion |
| **7. Migration Validation** | Automated test suites verify functional equivalence | Quantified confidence in the result |

---

## Why Success Rates Are Higher

Traditional migration approaches have a single point of validation: the end. If something is wrong, you discover it after all the investment has been made.

Migris inserts validation and risk reduction at **every stage**:

- ✓ Assessment catches blocking issues before conversion starts
- ✓ AI confidence scoring flags uncertain conversions before they're applied
- ✓ Human review catches semantic errors before they reach the database
- ✓ PgPassthrough enables application testing before conversion is complete
- ✓ Automated validation suites verify equivalence at the query level

Each checkpoint reduces the probability of downstream failure. The compounding effect is dramatic: instead of one pass/fail gate at the end, you have five opportunities to catch and correct issues when they are small and inexpensive to fix.

---

## Engagement Model

A typical Migris engagement follows this timeline:

| Week | Activity |
|------|----------|
| 1 | Assessment and readiness scoring |
| 2–3 | Pre-migration cleanup and preparation |
| 3–5 | AI-assisted schema conversion and human review |
| 5–6 | Data migration and PgPassthrough deployment |
| 6–8 | Application testing through PgPassthrough with progressive native conversion |
| 8–10 | Final validation and cutover planning |

Timelines vary based on database size and complexity. The assessment report produced in Week 1 provides accurate effort estimates for the full engagement.

---

## Summary

The Migris Database Migration Platform is not another schema converter. It is an end-to-end migration system designed around a single principle: **keep applications working**.

Every architectural decision — from pre-migration preparation, to application-aware conversion, to AI with human oversight, to the PgPassthrough testing proxy — serves that goal.

The result is higher success rates, shorter timelines, lower risk, and a migration process that your team can observe and validate from beginning to end.

---

<div style="text-align:center; margin-top:40px; color:#666; font-size:0.9em;">

**Migris Technology**

*Contact us to schedule an assessment of your SQL Server environment.*

</div>
