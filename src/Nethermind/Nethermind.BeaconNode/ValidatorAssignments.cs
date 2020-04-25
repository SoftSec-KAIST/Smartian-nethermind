﻿//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Api;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Logging.Microsoft;

namespace Nethermind.BeaconNode
{
    public class ValidatorAssignments
    {
        private readonly BeaconChainUtility _beaconChainUtility;
        private readonly BeaconStateAccessor _beaconStateAccessor;
        private readonly BeaconStateTransition _beaconStateTransition;
        private readonly IForkChoice _forkChoice;
        private readonly ILogger<ValidatorAssignments> _logger;
        private readonly IStore _store;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;

        public ValidatorAssignments(ILogger<ValidatorAssignments> logger,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            BeaconChainUtility beaconChainUtility,
            BeaconStateAccessor beaconStateAccessor,
            BeaconStateTransition beaconStateTransition,
            IForkChoice forkChoice,
            IStore store)
        {
            _logger = logger;
            _timeParameterOptions = timeParameterOptions;
            _beaconChainUtility = beaconChainUtility;
            _beaconStateAccessor = beaconStateAccessor;
            _beaconStateTransition = beaconStateTransition;
            _forkChoice = forkChoice;
            _store = store;
        }

        public bool CheckIfValidatorActive(BeaconState state, ValidatorIndex validatorIndex)
        {
            if ((int) validatorIndex >= state.Validators.Count)
            {
                return false;
            }

            Validator validator = state.Validators[(int) validatorIndex];
            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(state);
            bool isActive = _beaconChainUtility.IsActiveValidator(validator, currentEpoch);
            return isActive;
        }

        /// <summary>
        ///     Return the committee assignment in the ``epoch`` for ``validator_index``.
        ///     ``assignment`` returned is a tuple of the following form:
        ///     * ``assignment[0]`` is the list of validators in the committee
        ///     * ``assignment[1]`` is the index to which the committee is assigned
        ///     * ``assignment[2]`` is the slot at which the committee is assigned
        ///     Return None if no assignment.
        /// </summary>
        public CommitteeAssignment GetCommitteeAssignment(BeaconState state, Epoch epoch, ValidatorIndex validatorIndex)
        {
            Epoch nextEpoch = _beaconStateAccessor.GetCurrentEpoch(state) + Epoch.One;
            if (epoch > nextEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch,
                    $"Committee epoch cannot be greater than next epoch {nextEpoch}.");
            }

            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            Slot startSlot = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            ulong endSlot = startSlot + timeParameters.SlotsPerEpoch;
            for (Slot slot = startSlot; slot < endSlot; slot += Slot.One)
            {
                ulong committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(state, slot);
                for (CommitteeIndex index = CommitteeIndex.Zero;
                    index < new CommitteeIndex(committeeCount);
                    index += CommitteeIndex.One)
                {
                    IReadOnlyList<ValidatorIndex> committee =
                        _beaconStateAccessor.GetBeaconCommittee(state, slot, index);
                    if (committee.Contains(validatorIndex))
                    {
                        CommitteeAssignment committeeAssignment = new CommitteeAssignment(committee, index, slot);
                        return committeeAssignment;
                    }
                }
            }

            return CommitteeAssignment.None;
        }

        public async Task<ValidatorDuty> GetValidatorDutyAsync(BlsPublicKey validatorPublicKey, Epoch epoch)
        {
            // NOTE: A validator may have two proposal slots in an epoch (in small test networks),
            // however this routine will always only return the first one, i.e. always the same
            // return value for a given epoch with a given starting state.
            
            Root head = await _forkChoice.GetHeadAsync(_store).ConfigureAwait(false);
            BeaconState headState = await _store.GetBlockStateAsync(head).ConfigureAwait(false);

            Epoch currentEpoch = _beaconStateAccessor.GetCurrentEpoch(headState);
            Epoch nextEpoch = currentEpoch + Epoch.One;

            if (epoch == Epoch.None)
            {
                epoch = currentEpoch;
            }
            else if (epoch > nextEpoch)
            {
                throw new ArgumentOutOfRangeException(nameof(epoch), epoch,
                    $"Duties cannot look ahead more than the next epoch {nextEpoch}.");
            }

            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;

            Slot slotToCheck = _beaconChainUtility.ComputeStartSlotOfEpoch(epoch);
            Slot endSlotExclusive = slotToCheck + new Slot(timeParameters.SlotsPerEpoch);

            Root rootForStart = await _store.GetAncestorAsync(head, slotToCheck);
            BeaconState storedState = await _store.GetBlockStateAsync(rootForStart);

            // Clone, so that it can be safely mutated (transitioned forward)
            BeaconState state = BeaconState.Clone(storedState);

            // Transition to start slot, of target epoch (may have been a skip slot, i.e. stored state may have been older)
            _beaconStateTransition.ProcessSlots(state, slotToCheck);

            // Check validator is valid.
            ValidatorIndex validatorIndex = CheckValidatorIndex(state, validatorPublicKey);

            Duty duty = new Duty()
            {
                AttestationSlot = Slot.None,
                AttestationCommitteeIndex = CommitteeIndex.None,
                BlockProposalSlot = Slot.None
            };

            // Check starting state
            duty = CheckStateDuty(state, validatorIndex, duty);
            slotToCheck += Slot.One;

            // Check other slots in epoch, if needed
            while (slotToCheck < endSlotExclusive && (!duty.AttestationSlot.HasValue || !duty.BlockProposalSlot.HasValue))
            {
                _beaconStateTransition.ProcessSlots(state, slotToCheck);
                duty = CheckStateDuty(state, validatorIndex, duty);
                slotToCheck += Slot.One;
            }

            if (!duty.AttestationSlot.HasValue)
            {
                if (_logger.IsWarn()) Log.ValidatorDoesNotHaveAttestationSlot(_logger, epoch, validatorPublicKey, null);
            }

            // HACK: Shards were removed from Phase 0, but analogy is committee index, so use for initial testing.
            Shard attestationShard = new Shard((ulong) duty.AttestationCommitteeIndex);

            ValidatorDuty validatorDuty =
                new ValidatorDuty(validatorPublicKey, duty.AttestationSlot, attestationShard, duty.BlockProposalSlot);
            return validatorDuty;
        }

        public bool IsProposer(BeaconState state, ValidatorIndex validatorIndex)
        {
            ValidatorIndex stateProposerIndex = _beaconStateAccessor.GetBeaconProposerIndex(state);
            return stateProposerIndex.Equals(validatorIndex);
        }

        private Duty CheckFutureSlots(BeaconState state, Slot endSlotExclusive, ValidatorIndex validatorIndex, Duty duty)
        {
            Slot nextSlot = state.Slot + Slot.One;
            while (nextSlot < endSlotExclusive && (!duty.AttestationSlot.HasValue || !duty.BlockProposalSlot.HasValue))
            {
                _beaconStateTransition.ProcessSlots(state, nextSlot);
                duty = CheckStateDuty(state, validatorIndex, duty);
                nextSlot += Slot.One;
            }

            return duty;
        }

        private async Task<Duty> CheckHistoricalSlotsAsync(IStore store, IReadOnlyList<Root> historicalBlockRoots,
            Slot fromSlot, Slot startSlotInclusive, ValidatorIndex validatorIndex, Duty duty)
        {
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            Slot previousSlot = fromSlot;
            while (true)
            {
                previousSlot -= Slot.One;
                int index = (int) (previousSlot % timeParameters.SlotsPerHistoricalRoot);
                Root previousRoot = historicalBlockRoots[index];
                BeaconState previousState = await store.GetBlockStateAsync(previousRoot).ConfigureAwait(false);

                duty = CheckStateDuty(previousState, validatorIndex, duty);

                if (previousSlot <= startSlotInclusive ||
                    (duty.AttestationSlot.HasValue && duty.BlockProposalSlot.HasValue))
                {
                    break;
                }
            }

            return duty;
        }

        private Duty CheckStateDuty(BeaconState state, ValidatorIndex validatorIndex, Duty duty)
        {
            // check attestation
            if (!duty.AttestationSlot.HasValue)
            {
                ulong committeeCount = _beaconStateAccessor.GetCommitteeCountAtSlot(state, state.Slot);
                for (CommitteeIndex index = CommitteeIndex.Zero;
                    index < new CommitteeIndex(committeeCount);
                    index += CommitteeIndex.One)
                {
                    IReadOnlyList<ValidatorIndex> committee =
                        _beaconStateAccessor.GetBeaconCommittee(state, state.Slot, index);
                    if (committee.Contains(validatorIndex))
                    {
                        duty.AttestationSlot = state.Slot;
                        duty.AttestationCommitteeIndex = index;
                    }
                }
            }

            // check proposer
            if (!duty.BlockProposalSlot.HasValue)
            {
                bool isProposer = IsProposer(state, validatorIndex);
                if (isProposer)
                {
                    duty.BlockProposalSlot = state.Slot;
                }
            }

            return duty;
        }

        private ValidatorIndex CheckValidatorIndex(BeaconState state, BlsPublicKey validatorPublicKey)
        {
            ValidatorIndex validatorIndex = FindValidatorIndexByPublicKey(state, validatorPublicKey);
            if (validatorIndex == ValidatorIndex.None)
            {
                throw new ArgumentOutOfRangeException(nameof(validatorPublicKey), validatorPublicKey,
                    $"Could not find specified validator at slot {state.Slot}.");
            }

            bool validatorActive = CheckIfValidatorActive(state, validatorIndex);
            if (!validatorActive)
            {
                throw new Exception(
                    $"Validator {validatorPublicKey} (index {validatorIndex}) not not active at slot {state.Slot}.");
            }

            return validatorIndex;
        }

        private class Duty
        {
            public CommitteeIndex AttestationCommitteeIndex { get; set; }
            public Slot? AttestationSlot { get; set; }
            public Slot? BlockProposalSlot { get; set; }
        }

        private ValidatorIndex FindValidatorIndexByPublicKey(BeaconState state, BlsPublicKey validatorPublicKey)
        {
            for (int index = 0; index < state.Validators.Count; index++)
            {
                if (state.Validators[index].PublicKey.Equals(validatorPublicKey))
                {
                    return new ValidatorIndex((ulong) index);
                }
            }

            return ValidatorIndex.None;
        }
    }
}