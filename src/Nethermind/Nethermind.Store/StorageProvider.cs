﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;

namespace Nethermind.Store
{
    public class StorageProvider : IStorageProvider
    {
        internal const int StartCapacity = 1024;

        private readonly Dictionary<StorageAddress, Stack<int>> _cache = new Dictionary<StorageAddress, Stack<int>>();

        private readonly HashSet<StorageAddress> _committedThisRound = new HashSet<StorageAddress>();

        private readonly ILogger _logger;
        private readonly IDbProvider _dbProvider;
        private readonly IStateProvider _stateProvider;

        private readonly Dictionary<Address, StorageTree> _storages = new Dictionary<Address, StorageTree>();

        private int _capacity = StartCapacity;
        private Change[] _changes = new Change[StartCapacity];
        private int _currentPosition = -1;

        public StorageProvider(IDbProvider dbProvider, IStateProvider stateProvider, ILogger logger)
        {
            _dbProvider = dbProvider;
            _stateProvider = stateProvider;
            _logger = logger;
        }

        public byte[] Get(Address address, BigInteger index)
        {
            return GetThroughCache(new StorageAddress(address, index));
        }

        public void Set(Address address, BigInteger index, byte[] newValue)
        {
            StorageAddress storageAddress = new StorageAddress(address, index);
            PushUpdate(storageAddress, newValue);
        }

        public Keccak GetRoot(Address address)
        {
            return GetOrCreateStorage(address).RootHash;
        }

        public int TakeSnapshot()
        {
            _logger?.Debug($"  STORAGE SNAPSHOT {_currentPosition}");
            return _currentPosition;
        }

        public void Restore(int snapshot)
        {
            _logger?.Debug($"  RESTORING STORAGE SNAPSHOT {snapshot}");
            if (snapshot > _currentPosition)
            {
                throw new InvalidOperationException($"{nameof(StorageProvider)} tried to restore snapshot {snapshot} beyond current position {_currentPosition}");
            }

            List<Change> keptInCache = new List<Change>();

            for (int i = 0; i < _currentPosition - snapshot; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_cache[change.StorageAddress].Count == 1)
                {
                    if (_changes[_cache[change.StorageAddress].Peek()].ChangeType == ChangeType.JustCache)
                    {
                        int actualPosition = _cache[change.StorageAddress].Pop();
                        if (actualPosition != _currentPosition - i)
                        {
                            throw new InvalidOperationException($"Expected actual position {actualPosition} to be equal to {_currentPosition} - {i}");
                        }
                        
                        keptInCache.Add(change);
                        _changes[actualPosition] = null;
                        continue;
                    }
                }

                int forAssertion = _cache[change.StorageAddress].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                _changes[_currentPosition - i] = null;

                if (_cache[change.StorageAddress].Count == 0)
                {
                    _cache.Remove(change.StorageAddress);
                }
            }

            _currentPosition = snapshot;
            foreach (Change kept in keptInCache)
            {
                _currentPosition++;
                _changes[_currentPosition] = kept;
                _cache[kept.StorageAddress].Push(_currentPosition);
            }
        }

        public void Commit(IReleaseSpec spec)
        {
            if (_currentPosition == -1)
            {
                _logger?.Debug("  NO STORAGE CHANGES TO COMMIT");
                return;
            }
            
            _logger?.Debug("  COMMITTING STORAGE CHANGES");

            if (_changes[_currentPosition] == null)
            {
                throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(StorageProvider)}");
            }
            
            if (_changes[_currentPosition + 1] != null)
            {
                throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(StorageProvider)}");
            }

            HashSet<Address> toUpdateRoots = new HashSet<Address>();

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (_committedThisRound.Contains(change.StorageAddress))
                {
                    continue;
                }

                int forAssertion = _cache[change.StorageAddress].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                _committedThisRound.Add(change.StorageAddress);
                toUpdateRoots.Add(change.StorageAddress.Address);

                switch (change.ChangeType)
                {
                    case ChangeType.JustCache:
                        break;
                    case ChangeType.Update:

                        _logger?.Debug($"  UPDATE {change.StorageAddress.Address}_{change.StorageAddress.Index} V = {Hex.FromBytes(change.Value, true)}");

                        StorageTree tree = GetOrCreateStorage(change.StorageAddress.Address);
                        tree.Set(change.StorageAddress.Index, change.Value);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            foreach (Address address in toUpdateRoots)
            {
                // TODO: this is tricky... for EIP-158
                if (_stateProvider.AccountExists(address))
                {
                    Keccak root = GetRoot(address);
                    _stateProvider.UpdateStorageRoot(address, root);
                }
            }

            _capacity = 1024;
            _changes = new Change[_capacity];
            _currentPosition = -1;
            _committedThisRound.Clear();
            _cache.Clear();
        }

        public void ClearCaches()
        {
            _logger?.Debug("  CLEARING STORAGE PROVIDER CACHES");

            _cache.Clear();
            _currentPosition = -1;
            _committedThisRound.Clear();
            Array.Clear(_changes, 0, _changes.Length);
            _storages.Clear();
        }

        private StorageTree GetOrCreateStorage(Address address)
        {
            if (!_storages.ContainsKey(address))
            {
                _storages[address] = new StorageTree(_dbProvider.GetOrCreateStorageDb(address), _stateProvider.GetStorageRoot(address));
            }

            return GetStorage(address);
        }

        private StorageTree GetStorage(Address address)
        {
            return _storages[address];
        }

        private byte[] GetThroughCache(StorageAddress storageAddress)
        {
            if (_cache.ContainsKey(storageAddress))
            {
                return _changes[_cache[storageAddress].Peek()].Value;
            }

            return GetAndAddToCache(storageAddress);
        }

        private byte[] GetAndAddToCache(StorageAddress storageAddress)
        {
            StorageTree tree = GetOrCreateStorage(storageAddress.Address);
            byte[] value = tree.Get(storageAddress.Index);
            PushJustCache(storageAddress, value);
            return value;
        }

        private void PushJustCache(StorageAddress address, byte[] value)
        {
            SetupCache(address);
            IncrementPosition();
            _cache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.JustCache, address, value);
        }

        private void PushUpdate(StorageAddress address, byte[] value)
        {
            SetupCache(address);
            IncrementPosition();
            _cache[address].Push(_currentPosition);
            _changes[_currentPosition] = new Change(ChangeType.Update, address, value);
        }

        private void IncrementPosition()
        {
            _currentPosition++;
            if (_currentPosition >= _capacity - 1) // sometimes we ask about the _currentPosition + 1;
            {
                _capacity *= 2;
                Array.Resize(ref _changes, _capacity);
            }
        }

        private void SetupCache(StorageAddress address)
        {
            if (!_cache.ContainsKey(address))
            {
                _cache[address] = new Stack<int>();
            }
        }

        private struct StorageAddress : IEquatable<StorageAddress>
        {
            public Address Address { get; }
            public BigInteger Index { get; }

            public StorageAddress(Address address, BigInteger index)
            {
                Address = address;
                Index = index;
            }

            public bool Equals(StorageAddress other)
            {
                return Equals(Address, other.Address) && Index.Equals(other.Index);
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj))
                {
                    return false;
                }
                return obj is StorageAddress address && Equals(address);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Address != null ? Address.GetHashCode() : 0) * 397) ^ Index.GetHashCode();
                }
            }
        }

        private class Change
        {
            public Change(ChangeType changeType, StorageAddress storageAddress, byte[] value)
            {
                StorageAddress = storageAddress;
                Value = value;
                ChangeType = changeType;
            }

            public ChangeType ChangeType { get; }
            public StorageAddress StorageAddress { get; }
            public byte[] Value { get; }
        }

        private enum ChangeType
        {
            JustCache,
            Update
        }
    }
}