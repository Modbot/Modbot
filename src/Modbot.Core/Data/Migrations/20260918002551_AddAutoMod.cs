using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modbot.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoMod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_ai_test_sample",
                table: "ai_test_sample");

            migrationBuilder.DropPrimaryKey(
                name: "pk_ai_test_run",
                table: "ai_test_run");

            migrationBuilder.DropPrimaryKey(
                name: "pk_ai_term_list",
                table: "ai_term_list");

            migrationBuilder.DropPrimaryKey(
                name: "pk_ai_rule_version",
                table: "ai_rule_version");

            migrationBuilder.DropPrimaryKey(
                name: "pk_ai_flag",
                table: "ai_flag");

            migrationBuilder.RenameTable(
                name: "ai_test_sample",
                newName: "automod_test_sample");

            migrationBuilder.RenameTable(
                name: "ai_test_run",
                newName: "automod_test_run");

            migrationBuilder.RenameTable(
                name: "ai_term_list",
                newName: "automod_term_list");

            migrationBuilder.RenameTable(
                name: "ai_rule_version",
                newName: "automod_rule_version");

            migrationBuilder.RenameTable(
                name: "ai_flag",
                newName: "automod_flag");

            migrationBuilder.RenameColumn(
                name: "ai_moderation_profile_facts_read_through",
                table: "settings",
                newName: "automod_profile_facts_read_through");

            migrationBuilder.RenameColumn(
                name: "ai_moderation_enabled",
                table: "settings",
                newName: "automod_enabled");

            migrationBuilder.RenameIndex(
                name: "ix_ai_test_sample_rule",
                table: "automod_test_sample",
                newName: "ix_automod_test_sample_rule");

            migrationBuilder.RenameIndex(
                name: "ix_ai_test_run_rule",
                table: "automod_test_run",
                newName: "ix_automod_test_run_rule");

            migrationBuilder.RenameIndex(
                name: "ux_ai_term_list_hub_id",
                table: "automod_term_list",
                newName: "ux_automod_term_list_hub_id");

            migrationBuilder.RenameIndex(
                name: "ux_ai_rule_version",
                table: "automod_rule_version",
                newName: "ux_automod_rule_version");

            migrationBuilder.RenameIndex(
                name: "ix_ai_flag_state",
                table: "automod_flag",
                newName: "ix_automod_flag_state");

            migrationBuilder.RenameIndex(
                name: "ix_ai_flag_rule_person",
                table: "automod_flag",
                newName: "ix_automod_flag_rule_person");

            migrationBuilder.AddColumn<string>(
                name: "automod_ai_tools",
                table: "settings",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "group_ban",
                table: "ai_topic",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "group_remove",
                table: "ai_topic",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "group_ban",
                table: "automod_term_list",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "group_remove",
                table: "automod_term_list",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ai_opinion",
                table: "automod_flag",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ai_opinion_at",
                table: "automod_flag",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ai_opinion_call_id",
                table: "automod_flag",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_opinion_reason",
                table: "automod_flag",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ai_proposed_action",
                table: "automod_flag",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "group_banned",
                table: "automod_flag",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "group_removed",
                table: "automod_flag",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "would_group_ban",
                table: "automod_flag",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "would_group_remove",
                table: "automod_flag",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddPrimaryKey(
                name: "pk_automod_test_sample",
                table: "automod_test_sample",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_automod_test_run",
                table: "automod_test_run",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_automod_term_list",
                table: "automod_term_list",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_automod_rule_version",
                table: "automod_rule_version",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_automod_flag",
                table: "automod_flag",
                column: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_automod_test_sample",
                table: "automod_test_sample");

            migrationBuilder.DropPrimaryKey(
                name: "pk_automod_test_run",
                table: "automod_test_run");

            migrationBuilder.DropPrimaryKey(
                name: "pk_automod_term_list",
                table: "automod_term_list");

            migrationBuilder.DropPrimaryKey(
                name: "pk_automod_rule_version",
                table: "automod_rule_version");

            migrationBuilder.DropPrimaryKey(
                name: "pk_automod_flag",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "automod_ai_tools",
                table: "settings");

            migrationBuilder.DropColumn(
                name: "group_ban",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "group_remove",
                table: "ai_topic");

            migrationBuilder.DropColumn(
                name: "group_ban",
                table: "automod_term_list");

            migrationBuilder.DropColumn(
                name: "group_remove",
                table: "automod_term_list");

            migrationBuilder.DropColumn(
                name: "ai_opinion",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "ai_opinion_at",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "ai_opinion_call_id",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "ai_opinion_reason",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "ai_proposed_action",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "group_banned",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "group_removed",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "would_group_ban",
                table: "automod_flag");

            migrationBuilder.DropColumn(
                name: "would_group_remove",
                table: "automod_flag");

            migrationBuilder.RenameTable(
                name: "automod_test_sample",
                newName: "ai_test_sample");

            migrationBuilder.RenameTable(
                name: "automod_test_run",
                newName: "ai_test_run");

            migrationBuilder.RenameTable(
                name: "automod_term_list",
                newName: "ai_term_list");

            migrationBuilder.RenameTable(
                name: "automod_rule_version",
                newName: "ai_rule_version");

            migrationBuilder.RenameTable(
                name: "automod_flag",
                newName: "ai_flag");

            migrationBuilder.RenameColumn(
                name: "automod_profile_facts_read_through",
                table: "settings",
                newName: "ai_moderation_profile_facts_read_through");

            migrationBuilder.RenameColumn(
                name: "automod_enabled",
                table: "settings",
                newName: "ai_moderation_enabled");

            migrationBuilder.RenameIndex(
                name: "ix_automod_test_sample_rule",
                table: "ai_test_sample",
                newName: "ix_ai_test_sample_rule");

            migrationBuilder.RenameIndex(
                name: "ix_automod_test_run_rule",
                table: "ai_test_run",
                newName: "ix_ai_test_run_rule");

            migrationBuilder.RenameIndex(
                name: "ux_automod_term_list_hub_id",
                table: "ai_term_list",
                newName: "ux_ai_term_list_hub_id");

            migrationBuilder.RenameIndex(
                name: "ux_automod_rule_version",
                table: "ai_rule_version",
                newName: "ux_ai_rule_version");

            migrationBuilder.RenameIndex(
                name: "ix_automod_flag_state",
                table: "ai_flag",
                newName: "ix_ai_flag_state");

            migrationBuilder.RenameIndex(
                name: "ix_automod_flag_rule_person",
                table: "ai_flag",
                newName: "ix_ai_flag_rule_person");

            migrationBuilder.AddPrimaryKey(
                name: "pk_ai_test_sample",
                table: "ai_test_sample",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_ai_test_run",
                table: "ai_test_run",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_ai_term_list",
                table: "ai_term_list",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_ai_rule_version",
                table: "ai_rule_version",
                column: "id");

            migrationBuilder.AddPrimaryKey(
                name: "pk_ai_flag",
                table: "ai_flag",
                column: "id");
        }
    }
}
