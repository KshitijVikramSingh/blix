namespace Blix;

// "I host animations." Deliberately not extending IUpdateable — these are orthogonal
// capabilities. An animation host typically also is updateable (you need a tick to
// sample animations), but the interfaces stay independent so a future host could be
// driven externally without claiming the IUpdateable contract.
public interface IAnimated
{
    void AddAnimation(IAnimation animation);
}
