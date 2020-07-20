﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Common.Log;
using Lykke.Common.Log;
using MAVN.Persistence.PostgreSQL.Legacy;
using MAVN.Service.QuorumExplorer.Domain.DTOs;
using MAVN.Service.QuorumExplorer.Domain.Repositories;
using MAVN.Service.QuorumExplorer.Domain.Services;

namespace MAVN.Service.QuorumExplorer.DomainServices
{
    public class TransactionLogDecodingService : ITransactionLogDecodingService
    {
        private readonly IDecodingService _decodingService;
        private readonly IEventRepository _eventRepository;
        private readonly ILog _log;
        private readonly ITransactionRepository _transactionRepository;
        private readonly ITransactionRunner _transactionRunner;

        public TransactionLogDecodingService(
            IDecodingService decodingService,
            IEventRepository eventRepository,
            ILogFactory logFactory,
            ITransactionRepository transactionRepository,
            ITransactionRunner transactionRunner)
        {
            _decodingService = decodingService;
            _eventRepository = eventRepository;
            _log = logFactory.CreateLog(this);
            _transactionRepository = transactionRepository;
            _transactionRunner = transactionRunner;
        }

        public async Task<bool> DecodeNonDecodedTopicsAsync(
            int batchSize)
        {
            using (_log.BeginScope($"{nameof(DecodeNonDecodedTopicsAsync)}-{Guid.NewGuid()}"))
            {
                IReadOnlyCollection<TransactionLog> nonDecodedTransactionLogs;

                try
                {
                    #region Logging

                    _log.Debug($"Getting up to {batchSize} non-decoded transaction logs  with known ABIs.");

                    #endregion

                    nonDecodedTransactionLogs = (await _transactionRepository.GetNonDecodedTransactionLogsAsync(batchSize)).ToList();

                    #region Logging

                    _log.Debug($"Got {nonDecodedTransactionLogs.Count} non-decoded transaction logs with known ABIs.");

                    #endregion
                }
                catch (Exception e)
                {
                    #region Logging

                    _log.Error(e, $"Failed to get up to {batchSize} non-decoded transaction logs with known ABIs.");

                    #endregion

                    return false;
                }

                if (nonDecodedTransactionLogs.Count == 0)
                {
                    #region Logging

                    _log.Debug("No non-decoded transaction logs with known ABIs have been found.");

                    #endregion

                    return false;
                }

                List<Event> decodedEvents;

                try
                {
                    #region Logging

                    _log.Debug($"Decoding {nonDecodedTransactionLogs.Count} transaction logs.");

                    #endregion

                    decodedEvents = nonDecodedTransactionLogs.Select(x => _decodingService.DecodeTransactionLog(x)).ToList();

                    #region Logging

                    _log.Debug($"{nonDecodedTransactionLogs.Count} transaction logs have been decoded.");

                    #endregion
                }
                catch (Exception e)
                {
                    #region Logging

                    _log.Error(e, "Failed to decode transaction logs.");

                    #endregion

                    return false;
                }

                try
                {
                    await _transactionRunner.RunWithTransactionAsync(async (txContext) =>
                    {
                        await _eventRepository.SaveAsync(decodedEvents, txContext);

                        await _transactionRepository.SaveDecodedAsync(
                            nonDecodedTransactionLogs.Select(t => Tuple.Create(t.LogIndex, t.TransactionHash)));
                    });
                }
                catch (InvalidOperationException e)
                {
                    #region Logging

                    _log.Error(e, "Failed to commit transaction.");

                    #endregion

                    return false;
                }
                catch (Exception e)
                {
                    #region Logging

                    _log.Error(e, "Failed to save decoded transaction logs.");

                    #endregion

                    return false;
                }

                #region Logging

                _log.Info($"{nonDecodedTransactionLogs.Count} transaction logs have been decoded.");

                #endregion

                return true;
            }
        }
    }
}
