﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CFGLib {
	internal static class Helpers {
		public static IEnumerable<T> LookupEnumerable<TKey, T>(
			this IDictionary<TKey, ICollection<T>> dictionary,
			TKey key
		) {
			ICollection<T> retval;
			if (dictionary.TryGetValue(key, out retval)) {
				return retval;
			}
			return Enumerable.Empty<T>();
		}


		internal static Dictionary<TKey, TValue> BuildLookup<TKey, TValue, T2, TElm>(
			Func<IEnumerable<TElm>> getInputListOfElements,
			Func<TElm, TKey> getKeyFromElement,
			Func<TElm, T2> getValueFromElement,
			Func<TValue> newEnumerable,
			Action<TValue, T2> updateStored
		) {
			var dict = new Dictionary<TKey, TValue>();
			foreach (var production in getInputListOfElements()) {
				var key = getKeyFromElement(production);
				var value = getValueFromElement(production);
				TValue result;
				if (!dict.TryGetValue(key, out result)) {
					result = newEnumerable();
					dict[key] = result;
				}
				updateStored(result, value);
			}
			return dict;
		}
	}
	internal class Boxed<T> {
		public T Value;
		public Boxed(T value) {
			Value = value;
		}
	}
}
