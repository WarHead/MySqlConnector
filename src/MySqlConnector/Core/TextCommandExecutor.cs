using System;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using MySqlConnector.Logging;
using MySqlConnector.Protocol;
using MySqlConnector.Protocol.Serialization;
using MySqlConnector.Utilities;

namespace MySqlConnector.Core
{
	internal class TextCommandExecutor : ICommandExecutor
	{
		internal TextCommandExecutor(MySqlCommand command)
		{
			m_command = command;
		}

		public virtual async Task<int> ExecuteNonQueryAsync(string commandText, MySqlParameterCollection parameterCollection,
			IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			using (var reader = (MySqlDataReader) await ExecuteReaderAsync(commandText, parameterCollection, CommandBehavior.Default, ioBehavior, cancellationToken).ConfigureAwait(false))
			{
				do
				{
					while (await reader.ReadAsync(ioBehavior, cancellationToken).ConfigureAwait(false))
					{
					}
				} while (await reader.NextResultAsync(ioBehavior, cancellationToken).ConfigureAwait(false));
				return reader.RecordsAffected;
			}
		}

		public virtual async Task<object> ExecuteScalarAsync(string commandText, MySqlParameterCollection parameterCollection,
			IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			var hasSetResult = false;
			object result = null;
			using (var reader = (MySqlDataReader) await ExecuteReaderAsync(commandText, parameterCollection, CommandBehavior.SingleResult | CommandBehavior.SingleRow, ioBehavior, cancellationToken).ConfigureAwait(false))
			{
				do
				{
					var hasResult = await reader.ReadAsync(ioBehavior, cancellationToken).ConfigureAwait(false);
					if (!hasSetResult)
					{
						if (hasResult)
							result = reader.GetValue(0);
						hasSetResult = true;
					}
				} while (await reader.NextResultAsync(ioBehavior, cancellationToken).ConfigureAwait(false));
			}
			return result;
		}

		public virtual async Task<DbDataReader> ExecuteReaderAsync(string commandText, MySqlParameterCollection parameterCollection,
			CommandBehavior behavior, IOBehavior ioBehavior, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (Log.IsDebugEnabled())
				Log.Debug("Session{0} ExecuteBehavior {1} CommandText: {2}", m_command.Connection.Session.Id, ioBehavior, commandText);
			using (var payload = CreateQueryPayload(commandText, parameterCollection))
			using (m_command.RegisterCancel(cancellationToken))
			{
				m_command.Connection.Session.StartQuerying(m_command);
				m_command.LastInsertedId = -1;
				try
				{
					await m_command.Connection.Session.SendAsync(payload, ioBehavior, CancellationToken.None).ConfigureAwait(false);
					return await MySqlDataReader.CreateAsync(m_command, behavior, ioBehavior).ConfigureAwait(false);
				}
				catch (MySqlException ex) when (ex.Number == (int) MySqlErrorCode.QueryInterrupted && cancellationToken.IsCancellationRequested)
				{
					Log.Warn("Session{0} query was interrupted", m_command.Connection.Session.Id);
					throw new OperationCanceledException(cancellationToken);
				}
				catch (Exception ex) when (payload.ArraySegment.Count > 4_194_304 && (ex is SocketException || ex is IOException || ex is MySqlProtocolException))
				{
					// the default MySQL Server value for max_allowed_packet (in MySQL 5.7) is 4MiB: https://dev.mysql.com/doc/refman/5.7/en/server-system-variables.html#sysvar_max_allowed_packet
					// use "decimal megabytes" (to round up) when creating the exception message
					int megabytes = payload.ArraySegment.Count / 1_000_000;
					throw new MySqlException("Error submitting {0}MB packet; ensure 'max_allowed_packet' is greater than {0}MB.".FormatInvariant(megabytes), ex);
				}
			}
		}

		private PayloadData CreateQueryPayload(string commandText, MySqlParameterCollection parameterCollection)
		{
			var statementPreparerOptions = StatementPreparerOptions.None;
			if (m_command.Connection.AllowUserVariables || m_command.CommandType == CommandType.StoredProcedure)
				statementPreparerOptions |= StatementPreparerOptions.AllowUserVariables;
			if (m_command.Connection.DateTimeKind == DateTimeKind.Utc)
				statementPreparerOptions |= StatementPreparerOptions.DateTimeUtc;
			else if (m_command.Connection.DateTimeKind == DateTimeKind.Local)
				statementPreparerOptions |= StatementPreparerOptions.DateTimeLocal;
			if (m_command.CommandType == CommandType.StoredProcedure)
				statementPreparerOptions |= StatementPreparerOptions.AllowOutputParameters;

			switch (m_command.Connection.GuidFormat)
			{
			case MySqlGuidFormat.Char36:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatChar36;
				break;
			case MySqlGuidFormat.Char32:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatChar32;
				break;
			case MySqlGuidFormat.Binary16:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatBinary16;
				break;
			case MySqlGuidFormat.TimeSwapBinary16:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatTimeSwapBinary16;
				break;
			case MySqlGuidFormat.LittleEndianBinary16:
				statementPreparerOptions |= StatementPreparerOptions.GuidFormatLittleEndianBinary16;
				break;
			}

			var preparer = new StatementPreparer(commandText, parameterCollection, statementPreparerOptions);
			return new PayloadData(preparer.ParseAndBindParameters(), isPooled: true);
		}

		static readonly IMySqlConnectorLogger Log = MySqlConnectorLogManager.CreateLogger(nameof(TextCommandExecutor));

		readonly MySqlCommand m_command;
	}
}
