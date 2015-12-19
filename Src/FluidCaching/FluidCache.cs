using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Platform.Utility;

namespace FluidCaching
{
    /// <summary>
    /// FluidCache is a thread safe cache that automatically removes the items that have not been accessed for a long time.
    /// an object will never be removed if it has been accessed within the minAge timeSpan, else it will be removed if it
    /// is older than maxAge or the cache is beyond its desired size capacity.  A periodic check is made when accessing nodes that determines
    /// if the cache is out of date, and clears the cache (allowing new objects to be loaded upon next request). 
    /// </summary>
    /// 
    /// <remarks>
    /// Each Index provides dictionary key / value access to any object in cache, and has the ability to load any object that is
    /// not found. The Indexes use Weak References allowing objects in index to be garbage collected if no other objects are using them.
    /// The objects are not directly stored in indexes, rather, indexes hold Nodes which are linked list nodes. The LifespanMgr maintains
    /// a list of Nodes in each AgeBag which hold the objects and prevents them from being garbage collected.  Any time an object is retrieved 
    /// through a Index it is marked to belong to the current AgeBag.  When the cache gets too full/old the oldest age bag is emptied moving any 
    /// nodes that have been touched to the correct AgeBag and removing the rest of the nodes in the bag. Once a node is removed from the 
    /// LifespanMgr it becomes elegible for garbage collection.  The Node is not removed from the Indexes immediately.  If a Index retrieves the 
    /// node prior to garbage collection it is reinserted into the current AgeBag's Node list.  If it has already been garbage collected a new  
    /// object gets loaded.  If the Index size exceeds twice the capacity the index is cleared and rebuilt.  
    /// 
    /// !!!!! THERE ARE 2 DIFFERENT LOCKS USED BY CACHE - so care is required when altering code or you may introduce deadlocks !!!!!
    ///        order of lock nesting is LifespanMgr (Monitor) / Index (ReaderWriterLock)
    /// </remarks>
    public class FluidCache<T> where T : class
    {
        private const int LockTimeout = 30000;

        private readonly Dictionary<string, IIndexManagement<T>> indexList = new Dictionary<string, IIndexManagement<T>>();
        protected IsValid IsValid;
        private readonly LifespanMgr lifeSpan;
        private readonly int capacity;
        private int curCount;
        private int totalCount;

        /// <summary>Constructor</summary>
        /// <param name="capacity">the normal item limit for cache (Count may exeed capacity due to minAge)</param>
        /// <param name="minAge">the minimium time after an access before an item becomes eligible for removal, during this time
        /// the item is protected and will not be removed from cache even if over capacity</param>
        /// <param name="maxAge">the max time that an object will sit in the cache without being accessed, before being removed</param>
        /// <param name="isValid">delegate used to determine if cache is out of date.  Called before index access not more than once per 10 seconds</param>
        public FluidCache(int capacity, TimeSpan minAge, TimeSpan maxAge, IsValid isValid)
        {
            this.capacity = capacity;
            IsValid = isValid;
            lifeSpan = new LifespanMgr(this, minAge, maxAge);
        }

        /// <summary>Retrieve a index by name</summary>
        public IIndex<T, TKey> GetIndex<TKey>(string indexName)
        {
            IIndexManagement<T> index;
            return (indexList.TryGetValue(indexName, out index) ? index as IIndex<T, TKey> : null);
        }

        /// <summary>Retrieve a object by index name / key</summary>
        public Task<T> Get<TType>(string indexName, TType key, ItemLoader<T, TType> item = null)
        {
            IIndex<T, TType> index = GetIndex<TType>(indexName);
            return (index == null) ? Task.FromResult(default(T)) : index.GetItem(key, item);
        }

        /// <summary>AddAsNode a new index to the cache</summary>
        /// <typeparam name="TKey">the type of the key value</typeparam>
        /// <param name="indexName">the name to be associated with this list</param>
        /// <param name="getKey">delegate to get key from object</param>
        /// <param name="item">delegate to load object if it is not found in index</param>
        /// <returns>the newly created index</returns>
        public IIndex<T, TKey> AddIndex<TKey>(string indexName, GetKey<T, TKey> getKey, ItemLoader<T, TKey> item)
        {
            var index = new Index<TKey>(this, getKey, item);
            indexList[indexName] = index;
            return index;
        }

        /// <summary>
        /// AddAsNode an item to the cache (not needed if accessed by index)
        /// </summary>
        public void Add(T item)
        {
            Add(item);
        }

        /// <summary>
        /// AddAsNode an item to the cache
        /// </summary>
        private INode<T> AddAsNode(T item)
        {
            if (item == null)
            {
                return null;
            }

            // see if item is already in index
            INode<T> node = null;
            foreach (KeyValuePair<string, IIndexManagement<T>> keyValue in indexList)
            {
                if ((node = keyValue.Value.FindItem(item)) != null)
                {
                    break;
                }
            }

            // dupl is used to prevent total count from growing when item is already in indexes (only new Nodes)
            bool isDupl = (node != null && node.Value == item);
            if (!isDupl)
            {
                node = lifeSpan.Add(item);
            }

            // make sure node gets inserted into all indexes
            foreach (KeyValuePair<string, IIndexManagement<T>> keyValue in indexList)
            {
                if (!keyValue.Value.AddItem(node))
                {
                    isDupl = true;
                }
            }

            if (!isDupl)
            {
                Interlocked.Increment(ref totalCount);
            }

            return node;
        }

        /// <summary>Remove all items from cache</summary>
        public void Clear()
        {
            foreach (KeyValuePair<string, IIndexManagement<T>> keyValue in indexList)
            {
                keyValue.Value.ClearIndex();
            }
            lifeSpan.Clear();
        }

        /// <summary>Index provides dictionary key / value access to any object in cache</summary>
        private class Index<TKey> : IIndex<T, TKey>, IIndexManagement<T>
        {
            private readonly FluidCache<T> owner;
            private readonly Dictionary<TKey, WeakReference> _index;
            private readonly GetKey<T, TKey> _getKey;
            private readonly ItemLoader<T, TKey> loadItem;
            private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

            /// <summary>constructor</summary>
            /// <param name="owner">parent of index</param>
            /// <param name="getKey">delegate to get key from object</param>
            /// <param name="loadItem">delegate to load object if it is not found in index</param>
            public Index(FluidCache<T> owner, GetKey<T, TKey> getKey, ItemLoader<T, TKey> loadItem)
            {
                Debug.Assert(owner != null, "owner argument required");
                Debug.Assert(getKey != null, "GetKey delegate required");
                this.owner = owner;
                _index = new Dictionary<TKey, WeakReference>(this.owner.capacity*2);
                _getKey = getKey;
                this.loadItem = loadItem;
                RebuildIndex();
            }

            /// <summary>Getter for index</summary>
            /// <param name="key">key to find (or load if needed)</param>
            /// <returns>the object value associated with key, or null if not found & could not be loaded</returns>
            public async Task<T> GetItem(TKey key, ItemLoader<T, TKey> loadItem = null)
            {
                INode<T> node = GetNode(key);
                node?.Touch();

                ItemLoader<T, TKey> loader = loadItem ?? this.loadItem;

                if ((node?.Value == null) && (loader != null))
                {
                    node = owner.AddAsNode(await loader(key));
                }

                return node?.Value;
            }

            /// <summary>Delete object that matches key from cache</summary>
            /// <param name="key"></param>
            public void Remove(TKey key)
            {
                INode<T> node = GetNode(key);
                node?.Remove();

                owner.lifeSpan.CheckValid();
            }

            private INode<T> GetNode(TKey key)
            {
                return RWLock.GetReadLock(_lock, LockTimeout, delegate
                {
                    WeakReference value;
                    return (INode<T>) (_index.TryGetValue(key, out value) ? value.Target : null);
                });
            }

            /// <summary>try to find this item in the index and return Node</summary>
            public INode<T> FindItem(T item)
            {
                return GetNode(_getKey(item));
            }

            /// <summary>Remove all items from index</summary>
            public void ClearIndex()
            {
                RWLock.GetWriteLock(_lock, LockTimeout, delegate
                {
                    _index.Clear();
                    return true;
                });
            }

            /// <summary>AddAsNode new item to index</summary>
            /// <param name="item">item to add</param>
            /// <returns>was item key previously contained in index</returns>
            public bool AddItem(INode<T> item)
            {
                TKey key = _getKey(item.Value);
                return RWLock.GetWriteLock(_lock, LockTimeout, delegate
                {
                    bool isDup = _index.ContainsKey(key);
                    _index[key] = new WeakReference(item, false);
                    return isDup;
                });
            }

            /// <summary>removes all items from index and reloads each item (this gets rid of dead nodes)</summary>
            public int RebuildIndex()
            {
                lock (owner.lifeSpan)
                    return RWLock.GetWriteLock(_lock, LockTimeout, delegate
                    {
                        _index.Clear();
                        foreach (INode<T> item in owner.lifeSpan)
                        {
                            AddItem(item);
                        }
                        return _index.Count;
                    });
            }
        }

        private class LifespanMgr : IEnumerable<INode<T>>
        {
            /// <summary>container class used to hold nodes added within a descrete timeframe</summary>
            private class AgeBag
            {
                public DateTime startTime;
                public DateTime stopTime;
                public Node first;
            }

            /// <summary>LRUNodes is a linked list of items</summary>
            private class Node : INode<T>
            {
                private readonly LifespanMgr _mgr;
                public Node next;
                public AgeBag ageBag;

                /// <summary>constructor</summary>
                public Node(LifespanMgr mgr, T value)
                {
                    _mgr = mgr;
                    Value = value;
                    Interlocked.Increment(ref _mgr._owner.curCount);
                    Touch();
                }

                /// <summary>returns the object</summary>
                public T Value { get; private set; }

                /// <summary>Updates the status of the node to prevent it from being dropped from cache</summary>
                public void Touch()
                {
                    if (Value != null && ageBag != _mgr._currentBag)
                    {
                        if (ageBag == null)
                        {
                            lock (_mgr)
                                if (ageBag == null)
                                {
                                    // if node.AgeBag==null then the object is not currently managed by LifespanMgr so add it
                                    next = _mgr._currentBag.first;
                                    _mgr._currentBag.first = this;
                                    Interlocked.Increment(ref _mgr._owner.curCount);
                                }
                        }
                        ageBag = _mgr._currentBag;
                        Interlocked.Increment(ref _mgr._currentSize);
                    }
                    _mgr.CheckValid();
                }

                /// <summary>Removes the object from node, thereby removing it from all indexes and allows it to be garbage collected</summary>
                public void Remove()
                {
                    if (ageBag != null && Value != null)
                    {
                        Interlocked.Decrement(ref _mgr._owner.curCount);
                    }
                    Value = null;
                    ageBag = null;
                }
            }

            private readonly FluidCache<T> _owner;
            private readonly TimeSpan _minAge;
            private readonly TimeSpan _maxAge;
            private readonly TimeSpan _timeSlice;
            private DateTime _nextValidCheck;
            private readonly int _bagItemLimit;

            private readonly AgeBag[] _bags;
            private AgeBag _currentBag;
            private int _currentSize;
            private int _current;
            private int _oldest;
            private const int _size = 265; // based on 240 timeslices + 20 bags for ItemLimit + 5 bags empty buffer

            public LifespanMgr(FluidCache<T> owner, TimeSpan minAge, TimeSpan maxAge)
            {
                _owner = owner;
                int maxMS = Math.Min((int) maxAge.TotalMilliseconds, 12*60*60*1000); // max = 12 hours
                _minAge = minAge;
                _maxAge = TimeSpan.FromMilliseconds(maxMS);
                _timeSlice = TimeSpan.FromMilliseconds(maxMS/240.0); // max timeslice = 3 min
                _bagItemLimit = _owner.capacity/20; // max 5% of capacity per bag
                _bags = new AgeBag[_size];
                for (int loop = _size - 1; loop >= 0; --loop)
                {
                    _bags[loop] = new AgeBag();
                }
                OpenCurrentBag(DateTime.Now, 0);
            }

            public INode<T> Add(T value)
            {
                return new Node(this, value);
            }

            /// <summary>checks to see if cache is still valid and if LifespanMgr needs to do maintenance</summary>
            public void CheckValid()
            {
                DateTime now = DateTime.Now;
                // Note: Monitor.Enter(this) / Monitor.Exit(this) is the same as lock(this)... We are using Monitor.TryEnter() because it
                // does not wait for a lock, if lock is currently held then skip and let next Touch perform cleanup.
                if ((_currentSize > _bagItemLimit || now > _nextValidCheck) && Monitor.TryEnter(this))
                {
                    try
                    {
                        if ((_currentSize > _bagItemLimit || now > _nextValidCheck))
                        {
                            // if cache is no longer valid throw contents away and start over, else cleanup old items
                            if (_current > 1000000 || (_owner.IsValid != null && !_owner.IsValid()))
                            {
                                _owner.Clear();
                            }
                            else
                            {
                                CleanUp(now);
                            }
                        }
                    }
                    finally
                    {
                        Monitor.Exit(this);
                    }
                }
            }

            /// <summary>remove old items or items beyond capacity from LifespanMgr allowing them to be garbage collected</summary>
            /// <remarks>since we do not physically move items when touched we must check items in bag to determine if they should be deleted 
            /// or moved.  Also items that were removed by setting value to null get removed now.  Rremoving an item from LifespanMgr allows 
            /// it to be garbage collected.  If removed item is retrieved by index prior to GC then it will be readded to LifespanMgr.</remarks>
            public void CleanUp(DateTime now)
            {
                lock (this)
                {
                    //calculate how many items should be removed
                    DateTime maxAge = now.Subtract(_maxAge);
                    DateTime minAge = now.Subtract(_minAge);
                    int itemsToRemove = _owner.curCount - _owner.capacity;
                    AgeBag bag = _bags[_oldest%_size];
                    while (_current != _oldest &&
                           (_current - _oldest > _size - 5 || bag.startTime < maxAge ||
                            (itemsToRemove > 0 && bag.stopTime > minAge)))
                    {
                        // cache is still too big / old so remove oldest bag
                        Node node = bag.first;
                        bag.first = null;
                        while (node != null)
                        {
                            Node next = node.next;
                            node.next = null;
                            if (node.Value != null && node.ageBag != null)
                            {
                                if (node.ageBag == bag)
                                {
                                    // item has not been touched since bag was closed, so remove it from LifespanMgr
                                    ++itemsToRemove;
                                    node.ageBag = null;
                                    Interlocked.Decrement(ref _owner.curCount);
                                }
                                else
                                {
                                    // item has been touched and should be moved to correct age bag now
                                    node.next = node.ageBag.first;
                                    node.ageBag.first = node;
                                }
                            }
                            node = next;
                        }
                        // increment oldest bag
                        bag = _bags[(++_oldest)%_size];
                    }
                    OpenCurrentBag(now, ++_current);
                    CheckIndexValid();
                }
            }

            private void CheckIndexValid()
            {
                // if indexes are getting too big its time to rebuild them
                if (_owner.totalCount - _owner.curCount > _owner.capacity)
                {
                    foreach (KeyValuePair<string, IIndexManagement<T>> keyValue in _owner.indexList)
                    {
                        _owner.curCount = keyValue.Value.RebuildIndex();
                    }
                    _owner.totalCount = _owner.curCount;
                }
            }

            /// <summary>Remove all items from LifespanMgr and reset</summary>
            public void Clear()
            {
                lock (this)
                {
                    foreach (AgeBag bag in _bags)
                    {
                        Node node = bag.first;
                        bag.first = null;
                        while (node != null)
                        {
                            Node next = node.next;
                            node.next = null;
                            node.ageBag = null;
                            node = next;
                        }
                    }
                    // reset item counters 
                    _owner.curCount = _owner.totalCount = 0;
                    // reset age bags
                    OpenCurrentBag(DateTime.Now, _oldest = 0);
                }
            }

            /// <summary>ready a new current AgeBag for use and close the previous one</summary>
            private void OpenCurrentBag(DateTime now, int bagNumber)
            {
                lock (this)
                {
                    // close last age bag
                    if (_currentBag != null)
                    {
                        _currentBag.stopTime = now;
                    }
                    // open new age bag for next time slice
                    AgeBag currentBag = _bags[(_current = bagNumber)%_size];
                    currentBag.startTime = now;
                    currentBag.first = null;
                    _currentBag = currentBag;
                    // reset counters for CheckValid()
                    _nextValidCheck = now.Add(_timeSlice);
                    _currentSize = 0;
                }
            }

            /// <summary>Create item enumerator</summary>
            public IEnumerator<INode<T>> GetEnumerator()
            {
                for (int bagNumber = _current; bagNumber >= _oldest; --bagNumber)
                {
                    AgeBag bag = _bags[bagNumber];
                    // if bag.first == null then bag is empty or being cleaned up, so skip it!
                    for (Node node = bag.first; node != null && bag.first != null; node = node.next)
                    {
                        if (node.Value != null)
                        {
                            yield return node;
                        }
                    }
                }
            }

            /// <summary>Create item enumerator</summary>
            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        };
    }
}