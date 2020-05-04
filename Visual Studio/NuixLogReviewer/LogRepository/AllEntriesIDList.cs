using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NuixLogReviewer.LogRepository
{
    public class AllEntriesIDList : IList<long>
    {
        public AllEntriesIDList(int count)
        {
            Count = count;
        }

        public long this[int index] { get { return index + 1; } set { throw new NotImplementedException(); } }

        public int Count
        {
            get; private set;
        }

        public bool IsReadOnly => throw new NotImplementedException();

        public void Add(long item)
        {
            throw new NotImplementedException();
        }

        public void Clear()
        {
            throw new NotImplementedException();
        }

        public bool Contains(long item)
        {
            throw new NotImplementedException();
        }

        public void CopyTo(long[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        public IEnumerator<long> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return i + 1;
            }
        }

        public int IndexOf(long item)
        {
            throw new NotImplementedException();
        }

        public void Insert(int index, long item)
        {
            throw new NotImplementedException();
        }

        public bool Remove(long item)
        {
            throw new NotImplementedException();
        }

        public void RemoveAt(int index)
        {
            throw new NotImplementedException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }
    }
}
