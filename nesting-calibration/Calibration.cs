namespace Calibration;

// The shared nesting calibration shapes.
public static class Shapes
{
    // S1: no control structure.
    public static int Flat(int x)
    {
        return x + 1;
    }

    // S2: one control structure.
    public static string OneBranch(int x)
    {
        if (x == 0)
        {
            return "zero";
        }
        return "other";
    }

    // S3: a control structure inside one.
    public static string BranchInBranch(int x, int y)
    {
        if (x == 0)
        {
            if (y == 0)
            {
                return "both";
            }
            return "first_only";
        }
        return "neither";
    }

    // S4: three control structures deep.
    public static string ThreeDeep(int x, int y, int z)
    {
        if (x == 0)
        {
            if (y == 0)
            {
                if (z == 0)
                {
                    return "all";
                }
                return "two";
            }
            return "one";
        }
        return "none";
    }

    // S5: a closure in the function body.
    public static List<int> ClosureInBody(List<int> xs)
    {
        return xs.Select(x => x + 1).ToList();
    }

    // S6: a closure inside a branch.
    public static List<int> ClosureInBranch(List<int> xs)
    {
        if (xs.Count > 0)
        {
            return xs.Select(x => x + 1).ToList();
        }
        return new List<int>();
    }

    // S7: a control structure inside a closure.
    public static List<string> BranchInClosure(List<int> xs)
    {
        return xs.Select(x =>
        {
            if (x == 0)
            {
                return "zero";
            }
            return "other";
        }).ToList();
    }
}
