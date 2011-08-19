﻿//
// System.Threading.StParkStatus.cs
//
// Copyright 2011 Carlos Martins, Duarte Nunes
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Author: Duarte Nunes (duarte.m.nunes@gmail.com)
//

namespace System.Threading  
{
	internal static class StParkStatus 
	{
      internal const int Success = 0;
      internal const int Timeout = -2;
		internal const int Interrupted = -3;
		internal const int Cancelled = -4;
      internal const int Pending = -5;
		internal const int Inflated = -6;
      internal const int StateChange = Int32.MaxValue;
   }
}
