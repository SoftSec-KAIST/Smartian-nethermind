//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Config;
using Nethermind.Int256;
using Nethermind.Mev.Data;

namespace Nethermind.Mev
{
    public interface IMevConfig : IConfig
    {
        [ConfigItem(
            Description = "Defines whether the MEV bundles are allowed.",
            DefaultValue = "false")]
        bool Enabled { get; set; }

        [ConfigItem(
            Description = "Defines how long MEV bundles will be kept in memory by clients", 
            DefaultValue = "3600")]
        UInt256 BundleHorizon { get; set; }
        
        [ConfigItem(
            Description = "Defines the maximum number of MEV bundles that can be kept in memory by clients", 
            DefaultValue = "200")]
        int BundlePoolSize { get; set; }

        [ConfigItem(Description = "Defines the maximum number of MEV bundles to be included within a single block", DefaultValue = "1")]
        int MaxMergedBundles { get; set; }
    }
}
