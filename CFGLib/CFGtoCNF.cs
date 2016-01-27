﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("CFGLibTest")]
namespace CFGLib {
	/// <summary>
	/// A class to help transform a generic grammar into a CNF grammar
	/// </summary>
	internal class CFGtoCNF {
		private Grammar _grammar;
		private int _freshx = 0;
		private Nonterminal _startSymbol;

		internal CFGtoCNF(Grammar grammar) {
			_grammar = grammar;
		}

		/// <summary>
		/// Actually performs the conversion and returns a new CNF grammar based on the old grammar
		/// </summary>
		/// <param name="simplify"></param>
		/// <returns></returns>
		internal CNFGrammar Convert(bool simplify) {
			var productions = CloneGrammar(_grammar);
			StepStart(productions);
			StepTerm(productions);
			StepBin(productions);
			StepDel(productions);
			StepUnit(productions);

			var nonterminalProductions = new List<CNFNonterminalProduction>();
			var terminalProductions = new List<CNFTerminalProduction>();
			var producesEmptyWeight = 0.0;
			
			foreach (var production in productions) {
				if (production.Rhs.Count > 2) {
					throw new Exception("Didn't expect more than 2");
				} else if (production.Rhs.Count == 2) {
					nonterminalProductions.Add(new CNFNonterminalProduction(production));
				} else if (production.Rhs.Count == 1) {
					var rhs = production.Rhs[0];
					terminalProductions.Add(new CNFTerminalProduction(production));
				} else if (production.Rhs.Count == 0) {
					producesEmptyWeight += production.Weight;
						// GetGrammarFromProductionList(production, productions);
				}
			}

			return new CNFGrammar(nonterminalProductions, terminalProductions, producesEmptyWeight, _startSymbol, simplify);
		}

		private static ISet<BaseProduction> CloneGrammar(Grammar grammar) {
			return CloneProductions(grammar.Productions);
		}

		private static ISet<BaseProduction> CloneProductions(IEnumerable<BaseProduction> productions) {
			var result = new HashSet<BaseProduction>();
			foreach (var production in productions) {
				// var productions = grammar.Productions;
				result.Add(production.DeepClone());
			}
			return result;
		}

		/// <summary>
		/// Eliminate the start symbol from right-hand sides
		/// </summary>
		/// <param name="productions"></param>
		private void StepStart(ISet<BaseProduction> productions) {
			var fresh = GetFresh();
			productions.Add(
				// new Production(fresh, new Sentence { Nonterminal.Of("S") })
				new Production(fresh, new Sentence { _grammar.Start })
			);
			_startSymbol = fresh;
		}
		
		/// <summary>
		/// Eliminate rules with nonsolitary terminals
		/// </summary>
		/// <param name="productions"></param>
		private void StepTerm(ISet<BaseProduction> productions) {
			var newProductions = new List<BaseProduction>();
			var lookup = new Dictionary<Terminal, Nonterminal>();
			foreach (var production in productions) {
				if (production.Rhs.Count < 2) {
					continue;
				}
				for (int i = 0; i < production.Rhs.Count; i++) {
					var word = production.Rhs[i];
					if (word.IsNonterminal()) {
						continue;
					}
					Terminal terminal = (Terminal)word;
					Nonterminal fresh;
					if (!lookup.TryGetValue(terminal, out fresh)) {
						fresh = GetFresh();
						lookup[terminal] = fresh;
						newProductions.Add(
							new Production(fresh, new Sentence { terminal })
						);
					}
					production.Rhs[i] = fresh;
				}
			}
			productions.UnionWith(newProductions);
		}
		
		/// <summary>
		/// Eliminate right-hand sides with more than 2 nonterminals
		/// </summary>
		/// <param name="productions"></param>
		private void StepBin(ISet<BaseProduction> productions) {
			List<BaseProduction> finalProductions = new List<BaseProduction>();
			foreach (var production in productions) {
				if (production.Rhs.Count < 3) {
					finalProductions.Add(production);
					continue;
				}
				var rhs = production.Rhs;
				var curr = production.Lhs;
				for (int i = 0; i < rhs.Count - 2; i++) {
					var weight = (curr == production.Lhs) ? production.Weight : 1.0;
					var left = rhs[i];
					var newFresh = GetFresh();
					finalProductions.Add(
						new Production(curr, new Sentence { left, newFresh }, weight)
					);
					curr = newFresh;
				}
				finalProductions.Add(
					new Production(curr, new Sentence { rhs[rhs.Count - 2], rhs[rhs.Count - 1] })
				);
			}
			productions.Clear();
			productions.UnionWith(finalProductions);
		}

		/// <summary>
		/// Eliminate ε-rules
		/// </summary>
		/// <param name="productions"></param>
		private void StepDel(ISet<BaseProduction> productions) {
			var nullableProbabilities = GetNullable(productions);
			var newRules = new List<BaseProduction>();
			foreach (var production in productions) {
				var toAdd = Nullate(production, nullableProbabilities);
				RemoveExtraneousNulls(toAdd);
				newRules.AddRange(toAdd);
			}
			productions.Clear();
			productions.UnionWith(newRules);
		}

		/// <summary>
		/// Eliminate unit rules (e.g., &lt;X> -> &lt;Y>)
		/// </summary>
		/// <param name="productions"></param>
		private static void StepUnit(ISet<BaseProduction> productions) {
			var previouslyDeleted = new HashSet<ValueUnitProduction>();
			// TODO: maybe we shouldn't allow self loops?
			RemoveSelfLoops(productions);

			var toRemove = BaseGrammar.RemoveDuplicatesHelper(productions);
			foreach (var production in toRemove) {
				productions.Remove(production);
			}

			bool changed = true;
			while (changed) {
				changed = StepUnitOnce(productions, previouslyDeleted);
			}
		}

		private static void RemoveSelfLoops(ISet<BaseProduction> productions) {
			var toDelete = new List<BaseProduction>();
			foreach (var production in productions) {
				if (production.IsSelfLoop) {
					toDelete.Add(production);
				}
			}
			foreach (var item in toDelete) {
				productions.Remove(item);
			}			
		}

		private static bool StepUnitOnce(ISet<BaseProduction> productions, ISet<ValueUnitProduction> previouslyDeleted) {
			var table = BuildLookupTable(productions);
			var results = new HashSet<BaseProduction>(productions);
			// var result = new Dictionary<ValueUnitProduction, BaseProduction>();
			//foreach (var production in productions) {
			//	result[new ValueUnitProduction(production.Lhs, production.Rhs)] = production;
			//}
			var changed = false;

			foreach (var production in productions) {
				var lhs = production.Lhs;
				if (production.Rhs.Count != 1) {
					continue;
				}
				var rhs = production.Rhs[0];
				if (rhs.IsTerminal()) {
					continue;
				}
				// at this point we have a unit production lhs -> rhs
				changed = true;
				results.Remove(production);
				previouslyDeleted.Add(new ValueUnitProduction(lhs, (Nonterminal)rhs));
				var entries = table.LookupEnumerable((Nonterminal)rhs);
				var sum = entries.Sum((p) => p.Weight);
				foreach (var entry in entries) {
					var probThisEntry = entry.Weight / sum;
					var newProd = new Production(lhs, entry.Rhs, production.Weight * probThisEntry);

					// TODO: these exceptions probably mess up probability
					if (newProd.IsSelfLoop) {
						continue;
					}
					if (entry.Rhs.Count == 1) {
						if (entry.Rhs[0].IsNonterminal()) {
							var vup = new ValueUnitProduction(lhs, (Nonterminal)entry.Rhs[0]);
							if (previouslyDeleted.Contains(vup)) {
								//// we're trying to add vup.Lhs -> vup.Rhs
								//// but we don't want to add a unit we've already deleted
								//// we need to divide up the newProd.weight among all the 
								//// rules vup.Lhs -> X where vup.Rhs -> X
								//var destinations = table.LookupEnumerable(vup.Rhs);
								//foreach (var relayed in destinations) {
								//}
								continue;
							}
						}						
					}

					// results.Add(newProd);
					// TODO need a structure for this to make it fast
					bool found = false;
					foreach (var result in results) {
						if (result.Lhs != newProd.Lhs) {
							continue;
						}
						if (result.Rhs.SequenceEqual(newProd.Rhs)) {
							found = true;
							result.Weight += newProd.Weight;
							break;
						}
					}
					if (!found) {
						results.Add(newProd);
					}
				}
				break;
			}
			productions.Clear();
			productions.UnionWith(results);
			return changed;
		}

		// TODO: reused, need to pull out
		private static ISet<Nonterminal> GetNonterminals(ISet<BaseProduction> productions) {
			var hs = new HashSet<Nonterminal>();
			foreach (var production in productions) {
				hs.Add(production.Lhs);
				foreach (var word in production.Rhs) {
					var nonterminal = word as Nonterminal;
					if (nonterminal != null) {
						hs.Add(nonterminal);
					}
				}
			}
			return hs;
		}

		private static Dictionary<Nonterminal, double> GetNullable2(ISet<BaseProduction> originalProductions) {
			var probNulls = new Dictionary<Nonterminal, string>();

			var nonterminals = GetNonterminals(originalProductions).ToList();
			Dictionary<Nonterminal, int> RToJ = new Dictionary<Nonterminal, int>();
			for (int i = 0; i < nonterminals.Count; i++) {
				var R = nonterminals[i];
				RToJ[R] = i;
			}

			// our dictionary contains weights, need to convert to probabilities
			var weightTable = Helpers.BuildLookup(
				() => originalProductions,
				(p) => p.Lhs,
				(p) => p.Weight,
				() => new Boxed<double>(0.0),
				(o, n) => o.Value += n
			);

			foreach (var production in originalProductions) {
				var lhs = production.Lhs;
				var rhs = production.Rhs;

				if (!probNulls.ContainsKey(lhs)) {
					probNulls[lhs] = "0";
				}
				if (!rhs.OnlyNonterminals()) {
					continue;
				}
				if (rhs.Count == 0) {
					probNulls[lhs] += string.Format(" + {0}", production.Weight);
				}
				foreach (var word in rhs) {
					var nt = (Nonterminal)word;
					probNulls[lhs] += string.Format(" + {0} * p(X{1})", production.Weight, RToJ[nt]);
				}
			}

			foreach (var key in probNulls.Keys) {
				Console.WriteLine("X{0}: ({1}) / {2}", RToJ[key], probNulls[key], weightTable[key].Value);
			}
			return null;
		}

		/// <summary>
		/// Returns the set of all nonterminals that derive ε (in one or many steps)
		/// </summary>
		/// <param name="originalProductions"></param>
		/// <returns></returns>
		private static Dictionary<Nonterminal, double> GetNullable(ISet<BaseProduction> originalProductions) {
			var productions = CloneProductions(originalProductions);
			ISet<BaseProduction> newProductions = new HashSet<BaseProduction>();
			var nullableNonterminals = new Dictionary<Nonterminal, double>();
			var changed = true;
			while (changed) {
				changed = false;
				foreach (var production in productions) {
					if (production.IsEmpty) {
						if (!nullableNonterminals.ContainsKey(production.Lhs)) {
							nullableNonterminals[production.Lhs] = 0.0;
						}
						nullableNonterminals[production.Lhs] += production.Weight;
						changed = true;
						continue;
					}
					for (int i = production.Rhs.Count - 1; i >= 0; i--) {
						var word = production.Rhs[i];
						var nt = word as Nonterminal;
						if (nt == null) { continue; }
						if (nullableNonterminals.ContainsKey(nt)) {
							production.Rhs.RemoveAt(i);
							changed = true;
						}
					}
					newProductions.Add(production);
				}
				Helpers.Swap(ref productions, ref newProductions);
				newProductions.Clear();
			}

			// our dictionary contains weights, need to convert to probabilities
			var weightTable = Helpers.BuildLookup(
				() => originalProductions,
				(p) => p.Lhs,
				(p) => p.Weight,
				() => new Boxed<double>(0.0),
				(o, n) => o.Value += n
			);
			foreach (var key in nullableNonterminals.Keys.ToList()) {
				var weight = nullableNonterminals[key];
				Boxed<double> boxedSum;
				double sum = 0.0;
				if (weightTable.TryGetValue(key, out boxedSum)) {
					sum = boxedSum.Value;
				}
				nullableNonterminals[key] = weight / sum;
			}

			return nullableNonterminals;
		}

		/// <summary>
		/// Remove &lt;A> -> ε unless A is the start symbol 
		/// </summary>
		/// <param name="productions"></param>
		private void RemoveExtraneousNulls(List<BaseProduction> productions) {
			if (productions.Count == 0) {
				return;
			}
			for (int i = productions.Count - 1; i >= 0; i--) {
				var production = productions[i];
				if (production.Lhs == _startSymbol) {
					continue;
				}
				if (production.IsEmpty) {
					productions.RemoveAt(i);
				}
			}
		}

		/// <summary>
		/// From a production, derive a set of productions for each combination of skipping nullable nonterminals.
		/// E.g., for production S -> AbB and nullable {A, B}, we get productions
		/// S -> AbB | Ab | bB | b
		/// </summary>
		/// <returns></returns>
		private static List<BaseProduction> Nullate(BaseProduction originalProduction, Dictionary<Nonterminal, double> nullableProbabilities) {
			var results = new List<BaseProduction>();
			results.Add(originalProduction);
			if (originalProduction.IsEmpty) {
				return results;
			}
			for (int i = originalProduction.Rhs.Count - 1; i >= 0; i--) {
				var newResults = new List<BaseProduction>();
				foreach (var production in results) {
					var word = production.Rhs[i];
					var nt = word as Nonterminal;
					if (nt == null) { continue; }
					if (!nullableProbabilities.ContainsKey(nt)) {
						continue;
					}
					// var with = production.Clone();
					var without = production.DeepClone();
					without.Rhs.RemoveAt(i);
					newResults.Add(without);
					without.Weight *= nullableProbabilities[nt];
					// production.Weight *= 1.0 - nullableProbabilities[nt];
					production.Weight *= 1.0; // TODO: all wrong
				}
				results.AddRange(newResults);
			}
			// NullateAux(production, nullableSet, 0, result);

			return results;
		}

		// todo: horrible
		private Nonterminal GetFresh() {
			var originalNonterminals = _grammar.GetNonterminals();
			Nonterminal result;
			do {
				result = Nonterminal.Of("X_" + _freshx);
				_freshx++;
			} while (originalNonterminals.Contains(result));
			return result;
		}
		
		private static Dictionary<Nonterminal, ICollection<BaseProduction>> BuildLookupTable(ISet<BaseProduction> productions) {
			Dictionary<Nonterminal, ICollection<BaseProduction>> table;

			table = Helpers.BuildLookup(
				() => productions,
				(p) => p.Lhs,
				(p) => p,
				() => (ICollection<BaseProduction>)new List<BaseProduction>(),
				(l, p) => l.Add(p)
			);
			return table;
		}
	}
}
