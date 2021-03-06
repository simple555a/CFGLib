﻿using CFGLib.Parsers.Forests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CFGLib.Parsers.Earley {

	/*
	Inspired by
	 * Elizabeth Scott's 2008 paper "SPPF-Style Parsing From Earley Recognisers" (http://dx.doi.org/10.1016/j.entcs.2008.03.044) [ES2008]
	 * John Aycock and Nigel Horspool's 2002 paper "Practical Earley Parsing" (http://dx.doi.org/10.1093/comjnl/45.6.620) [AH2002]
	 * Loup Vaillant's tutorial (http://loup-vaillant.fr/tutorials/earley-parsing/)
	*/

	public class EarleyParser : Parser {
		private readonly BaseGrammar _grammar;
		private readonly double _probabilityChangePercentage = 1e-15;

		public EarleyParser(BaseGrammar grammar) {
			_grammar = grammar;
		}

		public override double ParseGetProbability(Sentence s) {
			var successes = ComputeSuccesses(s);
			if (successes.Count == 0) {
				return 0.0;
			}

			var internalSppf = ConstructInternalSppf(successes, s);
			AnnotateWithProductions(internalSppf);

			var nodeProbs = new Dictionary<SppfNode, double>();
			var prob = CalculateProbability(internalSppf, nodeProbs);

			//PrintForest(internalSppf, nodeProbs);
			//Console.WriteLine();
			//PrintDebugForest(internalSppf, s, nodeProbs);
			//var pp = new PrettyPrinter();
			//internalSppf.Accept(pp);
			//Console.WriteLine(pp.Result);

			return prob;
		}

		public override ForestInternal ParseGetForest(Sentence s) {
			var successes = ComputeSuccesses(s);
			if (successes.Count == 0) {
				return null;
			}

			var internalSppf = ConstructInternalSppf(successes, s);
			AnnotateWithProductions(internalSppf);

			//var nodeProbs = new Dictionary<SppfNode, double>();
			//var prob = CalculateProbability(internalSppf, nodeProbs);
			var nodes = GetAllNodes(internalSppf);
			var id = 0;
			foreach (var node in nodes) {
				node.Id = id;
				id++;
			}
			// PrintForest(internalSppf);
			//Console.WriteLine();
			//PrintDebugForest(internalSppf, s, nodeProbs);
			
			return new ForestInternal(internalSppf, internalSppf.Symbol);
		}

		private IList<Item> ComputeSuccesses(Sentence s) {
			// this helps sometimes
			//var incomingTerminals = s.GetAllTerminals();
			//var parseableTerminals = _grammar.GetTerminals();
			//if (!incomingTerminals.IsSubsetOf(parseableTerminals)) {
			//	return new List<Item>();
			//}

			var S = ComputeState(s);
			if (S == null) {
				return new List<Item>();
			}

			return GetSuccesses(S, s);
		}
		private StateSet[] ComputeState(Sentence s) {
			StateSet[] S = new StateSet[s.Count + 1];

			// Initialize S(0)
			S[0] = new StateSet(_grammar.Start);
			foreach (var production in _grammar.ProductionsFrom(_grammar.Start)) {
				var item = new Item(production, 0, 0);
				S[0].Insert(item);
			}

			// outer loop
			for (int stateIndex = 0; stateIndex < S.Length; stateIndex++) {
				var stateprob = S[stateIndex];

				// If there are no items in the current state, we're stuck
				if (stateprob.Count == 0) {
					return null;
				}

				var nextIndex = stateIndex + 1;
				if (nextIndex < S.Length) {
					S[nextIndex] = new StateSet();
				}
				StepState(S, s, stateIndex, stateprob);
			}
			
			// for those items we added magically, make sure they get treated by completion
			for (int stateIndex = 0; stateIndex < S.Length; stateIndex++) {
				foreach (var p in S[stateIndex].MagicItems) {
					foreach (var t in S[stateIndex]) {
						if (t.StartPosition != stateIndex) {
							continue;
						}
						if (t.Production.Lhs != p.PrevWord) {
							continue;
						}
						if (!t.IsComplete()) {
							continue;
						}
						p.AddReduction(stateIndex, t);
					}
				}
			}

			return S;
		}

		private void StepState(StateSet[] S, Sentence s, int stateIndex, StateSet state) {
			Terminal inputTerminal = null;
			if (stateIndex < s.Count) {
				inputTerminal = (Terminal)s[stateIndex];
			}

			// completion + initialization
			for (int itemIndex = 0; itemIndex < state.Count; itemIndex++) {
				var item = state[itemIndex];
				var nextWord = item.NextWord;
				if (nextWord == null) {
					Completion(S, stateIndex, item);
				} else if (nextWord.IsNonterminal) {
					Prediction(S, stateIndex, (Nonterminal)nextWord, item);
				} else {
					Scan(S, stateIndex, item, (Terminal)nextWord, s, inputTerminal);
				}
			}
		}

		private double CalculateProbability(SymbolNode sppf, Dictionary<SppfNode, double> nodeProbs) {
			var nodes = GetAllNodes(sppf);

			var indexToNode = nodes.ToArray();
			var nodeToIndex = new Dictionary<SppfNode, int>();
			for (int i = 0; i < indexToNode.Length; i++) {
				nodeToIndex[indexToNode[i]] = i;
			}

			var previousEstimates = Enumerable.Repeat(1.0, indexToNode.Length).ToArray();
			var currentEstimates = new double[indexToNode.Length];

			//for (var i = 0; i < indexToNode.Length; i++) {
			//	Console.WriteLine("{0,-40}: {1}", indexToNode[i], previousEstimates[i]);
			//}

			bool changed = true;
			while (changed == true) {
				changed = false;
				
				Array.Clear(currentEstimates, 0, currentEstimates.Length);

				for (var i = 0; i < indexToNode.Length; i++) {
					var node = indexToNode[i];
					var estimate = StepProbability(node, nodeToIndex, previousEstimates);
					currentEstimates[i] = estimate;

					if (currentEstimates[i] > previousEstimates[i]) {
						throw new Exception("Didn't expect estimates to increase");
					} else if (currentEstimates[i] < previousEstimates[i]) {
						var diff = previousEstimates[i] - currentEstimates[i];
						var tolerance = _probabilityChangePercentage * previousEstimates[i];
						if (diff > _probabilityChangePercentage) {
							changed = true;
						}
					}
				}
				
				//Console.WriteLine("--------------------------");
				//for (var i = 0; i < indexToNode.Length; i++) {
				//	Console.WriteLine("{0,-40}: {1}", indexToNode[i], currentEstimates[i]);
				//}

				Helpers.Swap(ref previousEstimates, ref currentEstimates);
			}

			for (var i = 0; i < indexToNode.Length; i++) {
				nodeProbs[indexToNode[i]] = currentEstimates[i];
			}

			return currentEstimates[nodeToIndex[sppf]];
		}
		
		private double StepProbability(SppfNode node, Dictionary<SppfNode, int> nodeToIndex, double[] previousEstimates) {
			if (node.Families.Count == 0) {
				return 1.0;
			}

			var l = node.Families;
			var familyProbs = new double[l.Count];
			for (int i = 0; i < l.Count; i++) {
				var alternative = l[i];
				
				double prob = GetChildProb(node, i);

				var childrenProbs = l[i].Members.Select((child) => previousEstimates[nodeToIndex[child]]).ToList();

				var childrenProb = childrenProbs.Aggregate(1.0, (p1, p2) => p1 * p2);

				familyProbs[i] = prob * childrenProb;
			}
			var familyProb = familyProbs.Sum();
			if (familyProb > 1) {
				familyProb = 1.0;
			}
			var result = familyProb;

			return result;
		}

		private double GetChildProb(SppfNode node, int i) {
			var production = node.Families[i].Production;
			var prob = 1.0;
			if (production != null) {
				prob = _grammar.GetProbability(production);
			}

			return prob;
		}

		private static HashSet<SppfNode> GetAllNodes(SymbolNode sppf) {
			var nodes = new HashSet<SppfNode>();
			var stack = new Stack<SppfNode>();

			stack.Push(sppf);
			while (stack.Count > 0) {
				var node = stack.Pop();
				if (nodes.Contains(node)) {
					continue;
				}
				nodes.Add(node);

				foreach (var family in node.Families) {
					foreach (var child in family.Members) {
						stack.Push(child);
					}
				}
			}

			return nodes;
		}

		#region annotate
		//TODO this is so horribly terrible. There's got to be a better way of thinking about this structure
		private void AnnotateWithProductions(SppfNode node, HashSet<SppfNode> seen = null, InteriorNode parent = null, int place = 0) {
			if (seen == null) {
				seen = new HashSet<SppfNode>();
			}

			if (node is IntermediateNode) {
				var intermediateNode = (IntermediateNode)node;
				var production = intermediateNode.Item.Production;
				if (intermediateNode.Item.CurrentPosition == production.Rhs.Count - 1) {
					parent.AddChild(place, production);
				}
			}
			
			if (seen.Contains(node)) {
				return;
			}
			seen.Add(node);
			
			var l = node.Families;
			for (int i = 0; i < l.Count; i++) {
				var alternative = l[i];
				
				if (!(node is InteriorNode)) {
					throw new Exception();
				}

				var members = l[i].Members;
				if (members.Count == 1) {
					var child = members[0];
					
					AnnotateWithProductionsChildren((InteriorNode)node, seen, child, i);
				} else if (members.Count == 2) {
					var left = members[0];
					var right = members[1];
					
					AnnotateWithProductionsChildren((InteriorNode)node, seen, left, right, i);
				} else {
					throw new Exception("Should only be 0--2 children");
				}
			}
		}
		
		private void AnnotateWithProductionsChildren(InteriorNode parent, HashSet<SppfNode> seen, SppfNode child, int place) {
			Word parentSymbol = null;
			if (parent is SymbolNode) {
				var symbolParent = (SymbolNode)parent;
				parentSymbol = symbolParent.Symbol;
			} else {
				var intermediateParent = (IntermediateNode)parent;
				if (intermediateParent.Item.CurrentPosition != 1) {
					throw new Exception("Expected to be at beginning of item");
				}
				parentSymbol = intermediateParent.Item.Production.Rhs[0];
			}

			if (child is SymbolNode) {
				var symbolChild = (SymbolNode)child;
				if (parent is SymbolNode) {
					var symbolParent = (SymbolNode)parent;
					var production = _grammar.FindProduction((Nonterminal)parentSymbol, new Sentence { symbolChild.Symbol });
					symbolParent.AddChild(place, production);
				}
				AnnotateWithProductions(symbolChild, seen, parent, place);
				return;
			} else if (child is IntermediateNode) {
				throw new Exception("Don't handle intermediate");
			} else if (child is LeafNode) {
				if (parentSymbol is Nonterminal) {
					var leafChild = (LeafNode)child;
					var childSentence = leafChild.GetSentence();
					var production = _grammar.FindProduction((Nonterminal)parentSymbol, childSentence);
					parent.AddChild(place, production);
				}
				return;
			}
			throw new Exception();
		}

		private void AnnotateWithProductionsChildren(InteriorNode parent, HashSet<SppfNode> seen, SppfNode left, SppfNode right, int place) {
			if (!(left is IntermediateNode)) {
				throw new Exception();
			}
			//if (!(right is SymbolNode)) {
			//	throw new Exception();
			//}

			AnnotateWithProductions(left, seen, parent, place);
			AnnotateWithProductions(right, seen, parent, place);
		}
#endregion annotate

		private SymbolNode ConstructInternalSppf(IEnumerable<Item> successes, Sentence s) {
			var root = new SymbolNode(_grammar.Start, 0, s.Count);
			var processed = new HashSet<Item>();
			var nodes = new Dictionary<SppfNode, SppfNode>();
			nodes[root] = root;
			
			foreach (var success in successes) {
				BuildTree(nodes, processed, root, success);
			}

			foreach (var node in nodes.Keys) {
				node.FinishFamily();
			}

			return root;
		}

		private void PrintForest(SppfNode node, Dictionary<SppfNode, double> nodeProbs = null, string padding = "", HashSet<SppfNode> seen = null) {
			if (seen == null) {
				seen = new HashSet<SppfNode>();
			}
			
			var nodeProb = "";
			if (nodeProbs != null) {
				nodeProb = " p=" + nodeProbs[node];
			}

			Console.WriteLine("{0}{1}{2}", padding, node, nodeProb);

			if (node.Families.Count > 0 && seen.Contains(node)) {
				Console.WriteLine("{0}Already seen this node!", padding);
				return;
			}
			seen.Add(node);
			
			//if (node is IntermediateNode) {
			//	foreach (var family in node.Families) {
			//		if (family.Production != null) {
			//			// throw new Exception();
			//		}
			//	}
			//	if (node.Families.Count > 1) {

			//	}
			//}

			var l = node.Families;

			for (int i = 0; i < l.Count; i++) {
				var alternative = l[i];
				if (l.Count > 1) {
					Console.WriteLine("{0}Alternative {1}", padding, i);
				}
				foreach (var member in l[i].Members) {
					PrintForest(member, nodeProbs, padding + "  ", seen);
				}
			}
		}

		private void PrintDebugForest(SppfNode node, Sentence s, Dictionary<SppfNode, double> nodeProbs = null, string padding = "", HashSet<SppfNode> seen = null) {
			if (seen == null) {
				seen = new HashSet<SppfNode>();
			}
			
			double? nodeProb = null;
			if (nodeProbs != null) {
				nodeProb = nodeProbs[node];
			}

			string lhs = "";
			if (node is SymbolNode) {
				var symbol = (SymbolNode)node;
				lhs = symbol.Symbol.ToString();
			} else if (node is IntermediateNode) {
				var inter = (IntermediateNode)node;
				lhs = inter.Item.ProductionToString();
			} else if (node is LeafNode) {
				lhs = ((LeafNode)node).GetSentence().ToString();
			} else {
				throw new Exception();
			}
			string rhs = "";
			if (node is InteriorNode) {
				var interior = (InteriorNode)node;
				rhs = s.GetRange(interior.StartPosition, interior.EndPosition - interior.StartPosition).ToString();
			}

			Console.WriteLine("{0}{1} --> {2} [{4}]\t{3}", padding, lhs, rhs, nodeProb, node.ProductionsToString());

			if (node.Families.Count > 0 && seen.Contains(node)) {
				Console.WriteLine("{0}Already seen this node!", padding);
				return;
			}
			seen.Add(node);

			if (node.Families.Count == 0) {
				return;
			}
			var l = node.Families;
			for (int i = 0; i < l.Count; i++) {
				var alternative = l[i];
				if (l.Count > 1) {
					Console.WriteLine("{0}Alternative {1}", padding, i);
				}
				foreach (var member in l[i].Members) {
					PrintDebugForest(member, s, nodeProbs, padding + "  ", seen);
				}
			}
		}

		// [Sec 4, ES2008]
		private void BuildTree(Dictionary<SppfNode, SppfNode> nodes, HashSet<Item> processed, InteriorNode node, Item item) {
			processed.Add(item);

			if (item.Production.Rhs.Count == 0) {
				var i = node.EndPosition;
				var v = NewOrExistingNode(nodes, new SymbolNode(item.Production.Lhs, i, i));
				//if there is no SPPF node v labeled (A, i, i)
				//create one with child node ϵ
				v.AddFamily(new Family(new EpsilonNode(i, i)));
				// basically, SymbolNodes with no children have empty children
			} else if (item.CurrentPosition == 1) {
				var prevWord = item.PrevWord;
				if (prevWord.IsTerminal) {
					var a = (Terminal)prevWord;
					var i = node.EndPosition;
					var v = NewOrExistingNode(nodes, new TerminalNode(a, i - 1, i));
					node.AddFamily(new Family(v));
				} else {
					var C = (Nonterminal)prevWord;
					var j = node.StartPosition;
					var i = node.EndPosition;
					var v = NewOrExistingNode(nodes, new SymbolNode(C, j, i));
					node.AddFamily(new Family(v));
					foreach (var reduction in item.Reductions) {
						if (reduction.Label != j) {
							continue;
						}
						var q = reduction.Item;
						if (!processed.Contains(q)) {
							BuildTree(nodes, processed, v, q);
						}
					}
				}
			} else if (item.PrevWord.IsTerminal) {
				var a = (Terminal)item.PrevWord;
				var j = node.StartPosition;
				var i = node.EndPosition;
				var v = NewOrExistingNode(nodes, new TerminalNode(a, i - 1, i));
				var w = NewOrExistingNode(nodes, new IntermediateNode(item.Decrement(), j, i - 1));
				foreach (var predecessor in item.Predecessors) {
					if (predecessor.Label != i - 1) {
						continue;
					}
					var pPrime = predecessor.Item;
					if (!processed.Contains(pPrime)) {
						BuildTree(nodes, processed, w, pPrime);
					}
				}

				node.AddFamily(new Family(w, v));
			} else {
				var C = (Nonterminal)item.PrevWord;
				foreach (var reduction in item.Reductions) {
					var l = reduction.Label;
					var q = reduction.Item;
					var j = node.StartPosition;
					var i = node.EndPosition;
					var v = NewOrExistingNode(nodes, new SymbolNode(C, l, i));
					if (!processed.Contains(q)) {
						BuildTree(nodes, processed, v, q);
					}
					var w = NewOrExistingNode(nodes, new IntermediateNode(item.Decrement(), j, l));
					foreach (var predecessor in item.Predecessors) {
						if (predecessor.Label != l) {
							continue;
						}
						var pPrime = predecessor.Item;
						if (!processed.Contains(pPrime)) {
							BuildTree(nodes, processed, w, pPrime);
						}
					}
					node.AddFamily(new Family(w, v));
				}
			}
		}

		private T NewOrExistingNode<T>(Dictionary<SppfNode, SppfNode> nodes, T node) where T : SppfNode {
			SppfNode existingNode;
			if (!nodes.TryGetValue(node, out existingNode)) {
				existingNode = node;
				nodes[node] = node;
			}
			node = (T)existingNode;
			return node;
		}

		private IList<Item> GetSuccesses(StateSet[] S, Sentence s) {
			var successes = new List<Item>();
			var lastState = S[s.Count];
			foreach (Item item in lastState) {
				if (!item.IsComplete()) {
					continue;
				}
				if (item.StartPosition != 0) {
					continue;
				}
				if (item.Production.Lhs != _grammar.Start) {
					continue;
				}
				successes.Add(item);
			}
			return successes;
		}

		private void Completion(StateSet[] S, int stateIndex, Item completedItem) {
			var state = S[stateIndex];
			var Si = S[completedItem.StartPosition];
			var toAdd = new List<Item>();
			foreach (var item in Si) {
				// make sure it's the same nonterminal
				if (item.NextWord != completedItem.Production.Lhs) {
					continue;
				}
				// for some reason, making sure it's the same prefix (tau) breaks everything.
				// this seems like a bug in [ES2008]
				// make sure it's the same prefix
				//var tau1 = completedItem.Production.Rhs.GetRange(0, completedItem.CurrentPosition);
				//var tau2 = item.Production.Rhs.GetRange(0, item.CurrentPosition);
				//if (!tau1.SequenceEqual(tau2)) {
				//	continue;
				//}
				var newItem = item.Increment();
				newItem.AddReduction(completedItem.StartPosition, completedItem);
				if (item.CurrentPosition != 0) {
					newItem.AddPredecessor(completedItem.StartPosition, item);
				}
				toAdd.Add(newItem);
			}
			foreach (var item in toAdd) {
				state.InsertWithoutDuplicating(item);
			}
		}
		private void Prediction(StateSet[] S, int stateIndex, Nonterminal nonterminal, Item item) {
			var state = S[stateIndex];
			// check if we've already predicted this nonterminal in this state, if so, don't
			// this optimization may not always be faster, but should help when there are lots of productions or high ambiguity
			if (!state.PredictedAlreadyAndSet(nonterminal)) {
				var productions = _grammar.ProductionsFrom(nonterminal);

				// insert, but avoid duplicates
				foreach (var production in productions) {
					var newItem = new Item(production, 0, stateIndex);
					// state.InsertWithoutDuplicating(stateIndex, newItem);
					// with the above optimization,
					// prediction can never introduce a duplicate item
					// its current marker is always 0, while completion
					// and scan generate items with nonzero current markers
					state.Insert(newItem);
				}
			}
			// If the thing we're trying to produce is nullable, go ahead and eagerly derive epsilon. [AH2002]
			// Except this trick only works easily when we don't want the full parse tree
			// we save items generated this way to use in completion later
			var probabilityNull = _grammar.NullableProbabilities[nonterminal];
			if (probabilityNull > 0.0) {
				var newItem = item.Increment();
				if (item.CurrentPosition != 0) {
					newItem.AddPredecessor(stateIndex, item);
				}
				var actualNewItem = state.InsertWithoutDuplicating(newItem);
				state.MagicItems.Add(actualNewItem);
			}
		}
		
		private void Scan(StateSet[] S, int stateIndex, Item item, Terminal terminal, Sentence s, Terminal currentTerminal) {
			var state = S[stateIndex];

			if (stateIndex + 1 >= S.Length) {
				return;
			}
			StateSet nextState = S[stateIndex + 1];

			if (currentTerminal == terminal) {
				var newItem = item.Increment();
				if (item.CurrentPosition != 0) {
					newItem.AddPredecessor(stateIndex, item);
				}
				// Scan can never insert a duplicate because it adds items to the next
				// StateSet, but never adds them more than once
				nextState.Insert(newItem);		
			}
		}
	}
}
